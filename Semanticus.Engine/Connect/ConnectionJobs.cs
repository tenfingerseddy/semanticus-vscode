using System;

namespace Semanticus.Engine
{
    /// <summary>
    /// Which saved model can do which job, in the engine's own words (slice C, contract 19.1). The Connections page
    /// strikes a model out of a job WITH this reason and never re-decides the rule, so the sentence a picker shows
    /// and the sentence the engine throws when the job is attempted are the same string, kept here once.
    /// </summary>
    public static class ConnectionJobs
    {
        public const string TestsFile = "A file on this computer cannot answer queries. Open it, then choose Power BI Desktop or a workspace model for tests and queries.";
        public const string PublishNotWorkspace = "Publish needs a workspace model. Power BI Desktop and files on this computer cannot be published to.";
        public const string PublishLoopback = "A model on this computer cannot be a publish destination. Choose a workspace model.";

        /// <summary>Why this saved model cannot answer tests and queries, or null when it can.</summary>
        public static string TestsRefusal(ModelConnectionRecord record) =>
            record != null && string.Equals(record.Kind, "file", StringComparison.OrdinalIgnoreCase) ? TestsFile : null;

        /// <summary>Why this saved model cannot be a publish destination, or null when it can.</summary>
        public static string PublishRefusal(ModelConnectionRecord record)
        {
            if (record == null) return null;
            if (!string.Equals(record.Kind, "xmla", StringComparison.OrdinalIgnoreCase)) return PublishNotWorkspace;
            return LiveDeploy.IsLocalEndpoint(record.Endpoint) ? PublishLoopback : null;
        }

        /// <summary>Fill both reasons on a record that is about to be listed. The record is a fresh read of
        /// connections.json, never the one a write goes back from, so nothing here reaches the disk.</summary>
        public static ModelConnectionRecord WithReasons(ModelConnectionRecord record)
        {
            record.TestsRefusal = TestsRefusal(record);
            record.PublishRefusal = PublishRefusal(record);
            return record;
        }
    }
}
