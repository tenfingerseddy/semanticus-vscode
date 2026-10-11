using System.Linq;
using Semanticus.Engine;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Verifier, lane m11/uat-numbers (N2, Sol H9). LEFT FAILING ON PURPOSE: it proves a defect, it does not fix one.
    ///
    /// The rows below are the live shape of DISCOVER_STORAGE_TABLE_COLUMNS, captured read-only from Northstar Sales
    /// Working on 2 Oct 2026 (one column shown of 55): the column's own BASIC_DATA row, its attribute hierarchy's two rows
    /// (COLUMN_ID POS_TO_ID and ID_TO_POS, ATTRIBUTE_NAME the column's name), the RowNumber row, and a relationship index
    /// row whose ATTRIBUTE_NAME is EMPTY and whose COLUMN_ID is "INDEX". Live, the lane reads 55 of 55: TABLE_ID carries
    /// the H$ / R$ prefix and the model's own column list was read. This holds the path the lane added for a server that
    /// refuses both: the TABLE_ID select falls back to the old one, and TMSCHEMA does not read. There the hierarchy rows
    /// fold into their column (count right), but a relationship index becomes a column called "INDEX".</summary>
    public sealed class VerifyUatNumbersVpaqFallbackTests
    {
        private static ResultSet Rs(string[] names, params object[][] rows) =>
            new ResultSet { Columns = names.Select(n => new ColumnDef { Name = n }).ToArray(), Rows = rows };

        [Fact]
        public void Without_table_id_or_the_models_list_a_relationship_index_is_never_counted_as_a_column()
        {
            var seg = Rs(new[] { "DIMENSION_NAME", "COLUMN_ID", "USED_SIZE" },
                new object[] { "Sales", "OrderNumber (45)", 100L },
                new object[] { "Sales", "POS_TO_ID", 7L },
                new object[] { "Sales", "ID_TO_POS", 7L },
                new object[] { "Sales", "INDEX", 9L });
            var col = Rs(new[] { "DIMENSION_NAME", "ATTRIBUTE_NAME", "COLUMN_ID", "COLUMN_ENCODING", "DICTIONARY_SIZE", "STRING_INDEX_SIZE" },
                new object[] { "Sales", "OrderNumber", "OrderNumber (45)", "2", 30L, 0L },
                new object[] { "Sales", "OrderNumber", "POS_TO_ID", "1", 1L, 0L },
                new object[] { "Sales", "OrderNumber", "ID_TO_POS", "1", 1L, 0L },
                new object[] { "Sales", "RowNumber-2662979B-1795-4F74-8F37-6A1BA8059B61", "RowNumber-2662979B-1795-4F74-8F37-6A1BA8059B61 (12)", "2", 0L, 0L },
                new object[] { "Sales", null, "INDEX", "1", 2L, 0L });
            var tbl = Rs(new[] { "DIMENSION_NAME", "ROWS_COUNT" }, new object[] { "Sales", 500L });

            var r = VertiPaq.Compute(seg, col, tbl, 25, VpaqStorageMode.Import, null,
                ResultSet.FromError("TMSCHEMA refused"), ResultSet.FromError("TMSCHEMA refused"));

            Assert.DoesNotContain(r.TopColumns, c => c.Column == "INDEX");
            Assert.Equal(1, r.ColumnCount);
        }
    }
}
