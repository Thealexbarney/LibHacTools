using LibHac;
using LibHac.FsSystem;

namespace SdkFinder
{
    internal class Options
    {
        public string InFile;
        public FileType InFileType = FileType.SearchSdk;
        public bool UseDevKeys;
        public bool EnableHash;
        public string Keyfile;
        public string TitleKeyFile;
        public string ConsoleKeyFile;
        public string AccessLog;
        public string ResultLog;
        public string OutDir;
        public string SdSeed;

        public IntegrityCheckLevel IntegrityLevel
        {
            get
            {
                if (EnableHash) return IntegrityCheckLevel.ErrorOnInvalid;
                return IntegrityCheckLevel.None;
            }
        }
    }

    internal enum FileType
    {
        SearchSdk
    }

    internal class Context
    {
        public Options Options;
        public Keyset Keyset;
        public Keyset DevKeyset;
        public ProgressBar Logger;
        public Horizon Horizon;
    }
}
