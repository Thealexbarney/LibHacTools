using System.Collections.Generic;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.FsSystem;

namespace SdkFinder
{
    public static class FsExtensions
    {
        public static IEnumerable<DirectoryEntryEx> EnumerateEntries(this FileSystemClient fs, string path, string searchPattern, SearchOptions searchOptions)
        {
            bool ignoreCase = searchOptions.HasFlag(SearchOptions.CaseInsensitive);
            bool recurse = searchOptions.HasFlag(SearchOptions.RecurseSubdirectories);

            Result rc = fs.OpenDirectory(out DirectoryHandle sourceHandle, path, OpenDirectoryMode.All);
            if (rc.IsFailure()) yield break;

            using (sourceHandle)
            {
                while (true)
                {
                    DirectoryEntry dirEntry = default;

                    fs.ReadDirectory(out long entriesRead, SpanHelpers.AsSpan(ref dirEntry), sourceHandle);
                    if (entriesRead == 0) break;

                    DirectoryEntryEx entry = GetDirectoryEntryEx(ref dirEntry, path);

                    if (PathTools.MatchesPattern(searchPattern, entry.Name, ignoreCase))
                    {
                        yield return entry;
                    }

                    if (entry.Type != DirectoryEntryType.Directory || !recurse) continue;

                    IEnumerable<DirectoryEntryEx> subEntries =
                        fs.EnumerateEntries(PathTools.Combine(path, entry.Name), searchPattern, searchOptions);

                    foreach (DirectoryEntryEx subEntry in subEntries)
                    {
                        yield return subEntry;
                    }
                }
            }
        }

        internal static DirectoryEntryEx GetDirectoryEntryEx(ref DirectoryEntry entry, string parentPath)
        {
            string name = StringUtils.Utf8ZToString(entry.Name);
            string path = PathTools.Combine(parentPath, name);

            var entryEx = new DirectoryEntryEx(name, path, entry.Type, entry.Size);
            entryEx.Attributes = entry.Attributes;

            return entryEx;
        }
    }
}
