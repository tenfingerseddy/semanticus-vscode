using System;
using System.IO;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    /// <summary>Whether a path sits inside a git repository, a fact an assistant must read and never guess. Git itself answers, so a folder on another
    /// filesystem from a repository above it, a bare <c>.git</c> folder that is no repository and a missing git all read as git would read them.</summary>
    public static class GitPresence
    {
        public static async Task<bool> InGitAsync(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return false;
                var full = Path.GetFullPath(path);
                var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
                return await GitCli.IsRepoAsync(dir).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch { return false; }   // no git, or no answer in time: not a repository we can name
        }
    }
}
