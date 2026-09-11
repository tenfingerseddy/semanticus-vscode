using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using TOM = Microsoft.AnalysisServices.Tabular;
using AS = Microsoft.AnalysisServices;

namespace Semanticus.Tests
{
    /// <summary>
    /// One recorded call the publish path made against the fake. The BIM text is the payload the engine
    /// would have sent to the live target.
    /// </summary>
    internal sealed class RecordedLiveCall
    {
        public bool Commit { get; init; }
        public bool Saved { get; init; }
        public string SessionBim { get; init; }
        public DeployReport Report { get; init; }
        public DateTime Utc { get; init; }
    }

    /// <summary>
    /// In-memory stand-in for an XMLA target. Records every payload the publish path sends, answers a
    /// scripted live state, and can be told the server changed under a preview. No real endpoint.
    /// Later cards wire this through <see cref="Attach"/> so both doors hit the same fake.
    /// </summary>
    internal sealed class RecordedLiveTarget
    {
        public const string Endpoint = "powerbi://api.powerbi.com/v1.0/myorg/ContosoSales";
        public const string DatabaseName = "V2";

        private readonly List<RecordedLiveCall> _calls = new List<RecordedLiveCall>();
        private TOM.Database _previewSnapshot;

        private RecordedLiveTarget(TOM.Database live) => Live = live ?? throw new ArgumentNullException(nameof(live));

        /// <summary>The live model the fake currently holds. A commit mutates this tree.</summary>
        public TOM.Database Live { get; }

        public IReadOnlyList<RecordedLiveCall> Calls => _calls;
        public int SaveChangesCount { get; private set; }
        public int ScriptedChanges { get; private set; }

        /// <summary>Five-table star used by later live-write cards: Date, Product, Customer, Store, Sales.</summary>
        public static RecordedLiveTarget FiveTable() => new RecordedLiveTarget(BuildStar(contosoDerived: false));

        /// <summary>Survey shape: the five-table star plus Sales[Order Date] active and Sales[Delivery Date] inactive.</summary>
        public static RecordedLiveTarget ContosoDerived() => new RecordedLiveTarget(BuildStar(contosoDerived: true));

        /// <summary>A deep copy of the current live state. Tests edit the copy and hand it back as the session.</summary>
        public TOM.Database CloneLive() => Clone(Live);

        /// <summary>A deep copy captured now. Workspace snapshot A vs B uses this so the two reads are independent trees.</summary>
        public TOM.Database Snapshot() => Clone(Live);

        public string WriteBim(TOM.Database db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            var path = Path.Combine(Path.GetTempPath(), "sem-live-" + Guid.NewGuid().ToString("N") + ".bim");
            File.WriteAllText(path, TOM.JsonSerializer.SerializeDatabase(db));
            return path;
        }

        /// <summary>Change the live model as if a colleague published while a preview was on screen.</summary>
        public void ChangeUnderPreview(Action<TOM.Model> mutate)
        {
            if (mutate == null) throw new ArgumentNullException(nameof(mutate));
            mutate(Live.Model);
            ScriptedChanges++;
        }

        /// <summary>Refs that differ between an earlier snapshot and the live model now. Empty means no drift.</summary>
        public string[] DriftSince(TOM.Database earlier)
        {
            if (earlier == null) throw new ArgumentNullException(nameof(earlier));
            return ModelCompare.Diff(earlier.Model, Live.Model, "before", "now").Items
                .Where(i => i.Action != "Equal")
                .Select(i => i.Ref)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        /// <summary>Drift against the live state captured at the last preview. Throws if no preview ran.</summary>
        public string[] DriftSincePreview()
        {
            if (_previewSnapshot == null)
                throw new InvalidOperationException("No preview has been recorded yet.");
            return DriftSince(_previewSnapshot);
        }

        public DeployReport Receive(string sessionBimPath, bool commit, IReadOnlyCollection<LiveDeleteTarget> deletes = null, IReadOnlyCollection<string> deleteRefs = null)
        {
            if (sessionBimPath == null) throw new ArgumentNullException(nameof(sessionBimPath));
            var bim = File.ReadAllText(sessionBimPath);
            var src = TOM.JsonSerializer.DeserializeDatabase(bim, null, AS.CompatibilityMode.PowerBI).Model;
            return ReceiveCore(src, bim, commit, deletes, deleteRefs);
        }

        public DeployReport Receive(TOM.Database session, bool commit, IReadOnlyCollection<LiveDeleteTarget> deletes = null, IReadOnlyCollection<string> deleteRefs = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var bim = TOM.JsonSerializer.SerializeDatabase(session);
            var src = TOM.JsonSerializer.DeserializeDatabase(bim, null, AS.CompatibilityMode.PowerBI).Model;
            return ReceiveCore(src, bim, commit, deletes, deleteRefs);
        }

        /// <summary>Point the engine's live-write seams at this fake. Both doors share those seams.</summary>
        public void Attach(LocalEngine engine)
        {
            if (engine == null) throw new ArgumentNullException(nameof(engine));
            engine.WorkspaceSnapshotHook = () => Task.FromResult(Snapshot());
            engine.WorkspacePushHook = (bim, dels) => Receive(bim, commit: true, dels);
            engine.DeployLiveSnapshotHook = () => TOM.JsonSerializer.SerializeDatabase(Live);
            engine.DeployLiveSyncHook = (bim, endpoint, database, commit, dels) => Receive(bim, commit, deleteRefs: dels);
        }

        private DeployReport ReceiveCore(TOM.Model src, string bim, bool commit, IReadOnlyCollection<LiveDeleteTarget> deletes, IReadOnlyCollection<string> deleteRefs = null)
        {
            if (!commit) _previewSnapshot = Clone(Live);
            var saved = false;
            var rep = LiveDeploy.SyncAndApply(src, Live.Model, commit, Endpoint, DatabaseName, deletes,
                saveChanges: () => { saved = true; SaveChangesCount++; },
                recalcCalcTables: _ => { }, identityStrict: false, explicitDeleteRefs: deleteRefs);
            _calls.Add(new RecordedLiveCall
            {
                Commit = commit,
                Saved = saved,
                SessionBim = bim,
                Report = rep,
                Utc = DateTime.UtcNow
            });
            return rep;
        }

        private static TOM.Database Clone(TOM.Database db) =>
            TOM.JsonSerializer.DeserializeDatabase(TOM.JsonSerializer.SerializeDatabase(db), null, AS.CompatibilityMode.PowerBI);

        private static TOM.Database BuildStar(bool contosoDerived)
        {
            var db = new TOM.Database(DatabaseName)
            {
                CompatibilityLevel = 1604,
                CompatibilityMode = AS.CompatibilityMode.PowerBI,
                Model = new TOM.Model()
            };

            var dateCol = AddDataTable(db.Model, "Date", "tag-date", new[]
            {
                Col("Date", TOM.DataType.DateTime, "tag-date-date", isKey: true, format: "yyyy-mm-dd"),
                Col("Year", TOM.DataType.Int64, "tag-date-year"),
                Col("Month Number", TOM.DataType.Int64, "tag-date-monthno"),
                Col("Month Name", TOM.DataType.String, "tag-date-monthname")
            });
            db.Model.Tables["Date"].DataCategory = "Time";
            db.Model.Tables["Date"].Columns["Month Name"].SortByColumn = db.Model.Tables["Date"].Columns["Month Number"];

            AddDataTable(db.Model, "Product", "tag-product", new[]
            {
                Col("ProductKey", TOM.DataType.Int64, "tag-product-key", isKey: true),
                Col("Product Name", TOM.DataType.String, "tag-product-name")
            });
            AddDataTable(db.Model, "Customer", "tag-customer", new[]
            {
                Col("CustomerKey", TOM.DataType.Int64, "tag-customer-key", isKey: true),
                Col("Customer Name", TOM.DataType.String, "tag-customer-name")
            });
            AddDataTable(db.Model, "Store", "tag-store", new[]
            {
                Col("StoreKey", TOM.DataType.Int64, "tag-store-key", isKey: true),
                Col("Store Name", TOM.DataType.String, "tag-store-name")
            });

            var salesDateName = contosoDerived ? "Order Date" : "Date";
            var salesCols = new List<ColSpec>
            {
                Col(salesDateName, TOM.DataType.DateTime, "tag-sales-date"),
                Col("ProductKey", TOM.DataType.Int64, "tag-sales-product"),
                Col("CustomerKey", TOM.DataType.Int64, "tag-sales-customer"),
                Col("StoreKey", TOM.DataType.Int64, "tag-sales-store"),
                Col("Quantity", TOM.DataType.Int64, "tag-sales-qty", hidden: true),
                Col("Amount", TOM.DataType.Decimal, "tag-sales-amount", hidden: true, format: "#,0.00")
            };
            if (contosoDerived)
                salesCols.Insert(1, Col("Delivery Date", TOM.DataType.DateTime, "tag-sales-delivery"));
            AddDataTable(db.Model, "Sales", "tag-sales", salesCols);
            var sales = db.Model.Tables["Sales"];
            sales.Measures.Add(new TOM.Measure
            {
                Name = "Total Sales",
                Expression = "SUM ( Sales[Amount] )",
                LineageTag = "tag-total-sales",
                Description = "The sum of all sales amounts across the model.",
                FormatString = "#,0"
            });

            Rel(db.Model, "Sales_Date", sales.Columns[salesDateName], dateCol, active: true);
            Rel(db.Model, "Sales_Product", sales.Columns["ProductKey"], db.Model.Tables["Product"].Columns["ProductKey"], active: true);
            Rel(db.Model, "Sales_Customer", sales.Columns["CustomerKey"], db.Model.Tables["Customer"].Columns["CustomerKey"], active: true);
            Rel(db.Model, "Sales_Store", sales.Columns["StoreKey"], db.Model.Tables["Store"].Columns["StoreKey"], active: true);
            if (contosoDerived)
                Rel(db.Model, "Sales_DeliveryDate", sales.Columns["Delivery Date"], dateCol, active: false);
            return db;
        }

        private readonly struct ColSpec
        {
            public string Name { get; init; }
            public TOM.DataType Type { get; init; }
            public string Tag { get; init; }
            public bool IsKey { get; init; }
            public bool Hidden { get; init; }
            public string Format { get; init; }
        }

        private static ColSpec Col(string name, TOM.DataType type, string tag, bool isKey = false, bool hidden = false, string format = null) =>
            new ColSpec { Name = name, Type = type, Tag = tag, IsKey = isKey, Hidden = hidden, Format = format };

        private static TOM.DataColumn AddDataTable(TOM.Model model, string name, string tag, IEnumerable<ColSpec> columns)
        {
            var table = new TOM.Table { Name = name, LineageTag = tag };
            table.Partitions.Add(new TOM.Partition
            {
                Name = name,
                Source = new TOM.MPartitionSource { Expression = "let Source = 1 in Source" }
            });
            TOM.DataColumn first = null;
            foreach (var spec in columns)
            {
                var col = new TOM.DataColumn
                {
                    Name = spec.Name,
                    SourceColumn = spec.Name,
                    DataType = spec.Type,
                    LineageTag = spec.Tag,
                    IsKey = spec.IsKey,
                    IsHidden = spec.Hidden
                };
                if (spec.Format != null) col.FormatString = spec.Format;
                if (spec.IsKey) col.SummarizeBy = TOM.AggregateFunction.None;
                table.Columns.Add(col);
                first ??= col;
            }
            model.Tables.Add(table);
            return first;
        }

        private static void Rel(TOM.Model model, string name, TOM.Column from, TOM.Column to, bool active)
        {
            model.Relationships.Add(new TOM.SingleColumnRelationship
            {
                Name = name,
                FromColumn = from,
                ToColumn = to,
                FromCardinality = TOM.RelationshipEndCardinality.Many,
                ToCardinality = TOM.RelationshipEndCardinality.One,
                IsActive = active
            });
        }
    }
}
