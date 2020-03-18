using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.FsSystem;
using LibHac.FsSystem.Detail;
using LibHac.FsSystem.NcaUtils;
using LibHac.Spl;

namespace SdkFinder
{
    internal class ProcessSearchSdk
    {
        private Context Context { get; }
        private Keyset Keyset { get; }
        private Keyset DevKeyset { get; }
        private FileSystemClient FsClient { get; }
        private List<NsoSet> NsoSets { get; } = new List<NsoSet>();
        private Dictionary<Buffer32, NsoInfo> Nsos { get; } = new Dictionary<Buffer32, NsoInfo>();
        private HashSet<string> LooseNsoSetsProcessed { get; } = new HashSet<string>();
        private byte[] Buffer { get; } = new byte[1024 * 1024 * 20];

        public ProcessSearchSdk(Context ctx)
        {
            Context = ctx;
            Keyset = ctx.Keyset;
            DevKeyset = ctx.DevKeyset;
            FsClient = ctx.Horizon.Fs;
        }

        public void Process()
        {
            if (Context.Options.OutDir == null)
            {
                Context.Logger.LogMessage("Output directory must be specified.");
                return;
            }

            var localFs = new LocalFileSystem(Context.Options.InFile);
            var outFs = new LocalFileSystem(Context.Options.OutDir);

            FsClient.Register("search".ToU8Span(), localFs);
            FsClient.Register("out".ToU8Span(), outFs);

            IEnumerable<DirectoryEntryEx> entries = FsClient.EnumerateEntries("search:/", "*.xci",
                    SearchOptions.CaseInsensitive | SearchOptions.RecurseSubdirectories)
                .Concat(FsClient.EnumerateEntries("search:/", "*.nsp",
                    SearchOptions.CaseInsensitive | SearchOptions.RecurseSubdirectories))
                .Concat(FsClient.EnumerateEntries("search:/", "*.nca",
                    SearchOptions.CaseInsensitive | SearchOptions.RecurseSubdirectories))
                .Concat(FsClient.EnumerateEntries("search:/", "*.nso",
                    SearchOptions.CaseInsensitive | SearchOptions.RecurseSubdirectories));
            {
                foreach (DirectoryEntryEx entry in entries)
                {
                    try
                    {
                        Context.Logger.LogMessage(entry.FullPath);

                        string extension = Path.GetExtension(entry.Name);

                        if (extension.ToLower() == ".xci")
                        {
                            ProcessXci(entry.FullPath);
                        }
                        else if (extension.ToLower() == ".nsp")
                        {
                            ProcessNsp(entry.FullPath);
                        }
                        else if (extension.ToLower() == ".nca")
                        {
                            ProcessNcaFile(entry.FullPath);
                        }
                        else if (extension.ToLower() == ".nso")
                        {
                            string parentDirectory = PathTools.GetParentDirectory(entry.FullPath);

                            // Don't process sets multiple times
                            if (LooseNsoSetsProcessed.Contains(parentDirectory))
                                continue;

                            if (IsNsoSetDirectory(parentDirectory))
                            {
                                ProcessNsoSetDirectory(parentDirectory);
                                LooseNsoSetsProcessed.Add(parentDirectory);
                            }
                            else
                            {
                                ProcessNso(entry.FullPath);

                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error processing {entry.FullPath}");
                        Console.WriteLine(ex);
                    }
                }
            }

            CalculateVersions();
            RenameOutput();

            FsClient.Unmount("search");
            FsClient.Unmount("out");
        }

        private void ProcessXci(string xciPath)
        {
            FsClient.OpenFile(out FileHandle xciHandle, xciPath, OpenMode.Read).ThrowIfFailure();

            using (var xciStorage = new FileHandleStorage(xciHandle, true))
            {
                var xci = new Xci(Keyset, xciStorage);

                if (!xci.HasPartition(XciPartitionType.Secure))
                    return;

                IFileSystem secureFs = xci.OpenPartition(XciPartitionType.Secure);
                ProcessNcaFs(secureFs);
            }
        }

        private void ProcessNsp(string nspPath)
        {
            FsClient.OpenFile(out FileHandle nspHandle, nspPath, OpenMode.Read).ThrowIfFailure();

            using (var nspStorage = new FileHandleStorage(nspHandle, true))
            {
                var pfs = new PartitionFileSystemCore<StandardEntry>();
                pfs.Initialize(nspStorage).ThrowIfFailure();

                ProcessNcaFs(pfs);
            }
        }

        private void ProcessNcaFile(string ncaPath)
        {
            FsClient.OpenFile(out FileHandle ncaHandle, ncaPath, OpenMode.Read).ThrowIfFailure();

            using (var ncaStorage = new FileHandleStorage(ncaHandle, true))
            {
                Nca nca = OpenNca(ncaStorage);

                ProcessNca(nca);
            }
        }

        private void ProcessNcaFs(IFileSystem ncaFs)
        {
            ImportTickets(ncaFs);

            foreach (DirectoryEntryEx fileEntry in ncaFs.EnumerateEntries("/", "*.nca"))
            {
                Result rc = ncaFs.OpenFile(out IFile ncaFile, fileEntry.FullPath, OpenMode.Read);
                if (rc.IsFailure()) continue;

                Nca nca = OpenNca(ncaFile.AsStorage());

                ProcessNca(nca);
            }
        }

        private void ProcessNso(string nsoPath)
        {
            FsClient.OpenFile(out FileHandle nsoHandle, nsoPath, OpenMode.Read).ThrowIfFailure();

            using (nsoHandle)
            using (var nsoStorage = new FileHandleStorage(nsoHandle, true))
            using (var nsoFile = new StorageFile(nsoStorage, OpenMode.Read))
            {
                var nso = new Nso(nsoStorage);
                NsoInfo info = GetNsoInfo((nso, nsoFile));

                ParseNsoName(nsoPath, info);
            }
        }

        // Import version data from the filename if needed
        private void ParseNsoName(string fileName, NsoInfo info)
        {
            if (Path.GetExtension(fileName) != ".nso")
                return;

            string[] splitName = Path.GetFileNameWithoutExtension(fileName).Split('-');
            if (splitName.Length != 4)
                return;

            string name = splitName[0];
            string version = splitName[1];
            string buildType = splitName[2];
            string id = splitName[3];

            if (name != info.Name || id != info.ShortBuildId)
                return;

            info.VersionString = version;
            info.BuildType = buildType;
        }

        private Nca OpenNca(IStorage ncaStorage)
        {
            try
            {
                return new Nca(Keyset, ncaStorage);
            }
            catch (InvalidDataException) { }

            return new Nca(DevKeyset, ncaStorage);
        }

        private void ProcessNca(Nca nca)
        {
            if (nca.CanOpenSection(NcaSectionType.Code))
            {
                IFileSystem codeFs = nca.OpenFileSystem(NcaSectionType.Code, IntegrityCheckLevel.ErrorOnInvalid);

                ProcessCodeFs(codeFs);
            }

            if (nca.CanOpenSection(NcaSectionType.Data))
            {
                int dataIndex = Nca.GetSectionIndexFromType(NcaSectionType.Data, nca.Header.ContentType);

                if (!nca.Header.GetFsHeader(dataIndex).IsPatchSection())
                {
                    IFileSystem dataFs = nca.OpenFileSystem(NcaSectionType.Data, IntegrityCheckLevel.ErrorOnInvalid);

                    foreach (DirectoryEntryEx fileEntry in dataFs.EnumerateEntries("/", "sdk",
                        SearchOptions.RecurseSubdirectories))
                    {
                        string dir = PathTools.GetParentDirectory(fileEntry.FullPath);

                        SubdirectoryFileSystem.CreateNew(out SubdirectoryFileSystem subFs, dataFs, dir.ToU8Span())
                            .ThrowIfFailure();

                        ProcessCodeFs(subFs);
                    }
                }
            }
        }

        private void ProcessCodeFs(IFileSystem fs)
        {
            var nsos = new List<IFile>();

            foreach (DirectoryEntryEx fileEntry in fs.EnumerateEntries("/", "rtld",
                SearchOptions.RecurseSubdirectories))
            {
                Result rc = fs.OpenFile(out IFile nsoFile, fileEntry.FullPath, OpenMode.Read);
                if (rc.IsSuccess())
                {
                    nsos.Add(nsoFile);
                }
            }

            foreach (DirectoryEntryEx fileEntry in fs.EnumerateEntries("/", "*sdk*",
                SearchOptions.RecurseSubdirectories))
            {
                Result rc = fs.OpenFile(out IFile nsoFile, fileEntry.FullPath, OpenMode.Read);
                if (rc.IsSuccess())
                {
                    nsos.Add(nsoFile);
                }
            }

            ProcessNsoSet(nsos);
        }

        private void ProcessNsoSet(List<IFile> list)
        {
            if (list.Count == 0) return;

            var nsos = new List<(Nso nso, IFile file)>();

            foreach (IFile file in list)
            {
                var nso = new Nso(file.AsStorage());
                nsos.Add((nso, file));
            }

            var nsoInfos = new List<NsoInfo>();
            foreach ((Nso nso, IFile file) nso in nsos)
            {
                try
                {
                    nsoInfos.Add(GetNsoInfo(nso));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing {nso.nso.BuildId.ToHexString()}");
                    Console.WriteLine(ex);
                }
            }

            var set = new NsoSet();
            set.Nsos.AddRange(nsoInfos);
            NsoSets.Add(set);

            Version maxVersion = set.Nsos.Where(x => x.Version != null).Select(x => x.Version).Max();

            if (maxVersion != null)
            {
                NsoInfo maxInfo = set.Nsos.First(x => x.Version == maxVersion);

                set.Version = maxVersion;
                set.MaxVersionNso = maxInfo;
            }

            foreach (NsoInfo info in nsoInfos)
            {
                info.Sets.Add(set);
            }
        }

        private NsoInfo GetNsoInfo((Nso nso, IFile file) nso)
        {
            Buffer32 buildId = Unsafe.As<byte, Buffer32>(ref nso.nso.BuildId[0]);

            if (!Nsos.TryGetValue(buildId, out NsoInfo info))
            {
                info = CreateNsoInfo(nso.nso);
                Nsos.Add(buildId, info);

                string fileName = $"out:/{buildId.ToString()}";

                nso.file.GetSize(out long nsoSize).ThrowIfFailure();
                Span<byte> nsoBytes = Buffer.AsSpan(0, (int)nsoSize);

                nso.file.Read(out long bytesRead, 0, nsoBytes).ThrowIfFailure();
                if (bytesRead != nsoSize) throw new InvalidDataException("Read incorrect number of bytes");

                FsClient.DeleteFile(fileName);
                FsClient.CreateFile(fileName, nsoSize).ThrowIfFailure();
                FsClient.OpenFile(out FileHandle outFile, fileName, OpenMode.Write).ThrowIfFailure();

                FsClient.WriteFile(outFile, 0, nsoBytes, WriteOption.Flush).ThrowIfFailure();
                FsClient.CloseFile(outFile);
            }

            return info;
        }

        private bool IsNsoSetDirectory(string path)
        {
            Result rc = FsClient.GetEntryType(out DirectoryEntryType type, $"{path}/nnSdk.nso");

            if (rc.IsFailure() || type != DirectoryEntryType.File)
                return false;

            rc = FsClient.GetEntryType(out type, $"{path}/nnrtld.nso");

            if (rc.IsFailure() || type != DirectoryEntryType.File)
                return false;

            return true;
        }

        private void ProcessNsoSetDirectory(string path)
        {
            var handles = new List<FileHandle>();
            var nsoFiles = new List<IFile>();
            try
            {
                foreach (DirectoryEntryEx fileEntry in FsClient.EnumerateEntries(path, "*.nso"))
                {
                    Result rc = FsClient.OpenFile(out FileHandle nsoHandle, fileEntry.FullPath, OpenMode.Read);

                    if (rc.IsSuccess())
                    {
                        var file = new StorageFile(new FileHandleStorage(nsoHandle, true), OpenMode.Read);

                        handles.Add(nsoHandle);
                        nsoFiles.Add(file);
                    }
                }

                ProcessNsoSet(nsoFiles);
            }
            finally
            {
                foreach (FileHandle handle in handles)
                {
                    FsClient.CloseFile(handle);
                }
            }
        }

        private string GetName(Nso nso)
        {
            byte[] rodata = nso.Sections[1].DecompressSection();

            if (rodata.Length < 9)
                return string.Empty;

            return StringUtils.Utf8ZToString(rodata.AsSpan(8));
        }

        private NsoInfo CreateNsoInfo(Nso nso)
        {
            var info = new NsoInfo();

            info.BuildId = Unsafe.As<byte, Buffer32>(ref nso.BuildId[0]);
            info.Name = GetName(nso);

            if (!TryGetBuildString(nso, out string buildString))
            {
                return info;
            }

            info.BuildString = buildString;

            string[] buildSplit = buildString.Split('-');
            if (buildSplit.Length != 3)
            {
                throw new InvalidDataException($"Unknown build string format {buildString}");
            }

            info.VersionString = buildSplit[1];
            info.BuildType = buildSplit[2];

            string[] versionSplit = info.VersionString.Split('_');
            if (versionSplit.Length != 3)
            {
                throw new InvalidDataException($"Unknown version string format {info.VersionString}");
            }

            info.Version = new Version(int.Parse(versionSplit[0]), int.Parse(versionSplit[1]), int.Parse(versionSplit[2]));

            return info;
        }

        private bool TryGetBuildString(Nso nso, out string buildString)
        {
            byte[] rodata = nso.Sections[1].DecompressSection();
            byte[] search = Encoding.ASCII.GetBytes("SDK MW");
            int searchLen = search.Length;

            for (int i = rodata.Length - searchLen; i > 0; i--)
            {
                Span<byte> data = rodata.AsSpan(i, searchLen);

                if (data.SequenceEqual(search))
                {
                    buildString = StringUtils.Utf8ZToString(rodata.AsSpan(i));
                    return true;
                }
            }

            buildString = default;
            return false;
        }

        private void CalculateVersions()
        {
            foreach (NsoInfo nso in Nsos.Values.Where(x => x.BuildString == null))
            {
                Version version = nso.Sets.Where(x => x.Version != null).Select(x => x.Version).Min();

                if (version != null)
                {
                    NsoInfo versionInfo = nso.Sets.First(x => x.Version == version).MaxVersionNso;

                    nso.VersionString = versionInfo.VersionString;
                    nso.Version = versionInfo.Version;
                    nso.BuildType = versionInfo.BuildType;
                }
            }
        }

        private void RenameOutput()
        {
            foreach (NsoInfo nso in Nsos.Values)
            {
                string oldName = $"out:/{nso.BuildId.ToString()}";
                string newName;

                string shortBuildId = nso.ShortBuildId;
                if (nso.VersionString != null)
                {
                    newName = $"out:/{nso.Name}-{nso.VersionString}-{nso.BuildType}-{shortBuildId}.nso";
                }
                else
                {
                    newName = $"out:/{nso.Name}-{shortBuildId}.nso";
                }

                Result rc = FsClient.RenameFile(oldName, newName);
                if (rc.IsFailure())
                {
                    Context.Logger.LogMessage($"Error {rc.ToStringWithName()} renaming {oldName} to {newName}");
                }
            }
        }

        private void ImportTickets(IFileSystem fs)
        {
            foreach (DirectoryEntryEx ticketEntry in fs.EnumerateEntries("/", "*.tik"))
            {
                Result result = fs.OpenFile(out IFile ticketFile, ticketEntry.FullPath, OpenMode.Read);

                if (result.IsSuccess())
                {
                    var ticket = new Ticket(ticketFile.AsStream());

                    Keyset.ExternalKeySet.Add(new RightsId(ticket.RightsId), new AccessKey(ticket.GetTitleKey(Keyset)));
                }
            }
        }

        [DebuggerDisplay("{" + nameof(Version) + "}")]
        private class NsoSet
        {
            public List<NsoInfo> Nsos { get; } = new List<NsoInfo>();
            public Version Version { get; set; }
            public NsoInfo MaxVersionNso { get; set; }
        }

        [DebuggerDisplay("{" + nameof(GetDisplay) + "()}")]
        private class NsoInfo
        {
            public Buffer32 BuildId;
            public string Name { get; set; }
            public string BuildString { get; set; }
            public string VersionString { get; set; }
            public Version Version { get; set; }
            public string BuildType { get; set; }
            public List<NsoSet> Sets { get; set; } = new List<NsoSet>();

            public string ShortBuildId => BuildId.ToString().Substring(0, 8);

            public string GetDisplay()
            {
                if (VersionString == null)
                {
                    return Name;
                }

                return $"{Name} {VersionString}-{BuildType}";
            }
        }
    }
}
