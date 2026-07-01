namespace OsuSkinMixer.Utils;

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OsuSkinMixer.Models;
using OsuSkinMixer.src.Models.Osu;
using OsuSkinMixer.Statics;

/// <summary>Base for classes that peform tasks based on a list of <see cref="SkinOption"/>. Provides abstract methods for populating tasks to be peformed on the relevant skin folders.</summary>
public abstract class SkinMachine : IDisposable
{
    private const int LOG_SPLIT_CHAR_SIZE = 100000;
    private const string MANIA_SKIN_INI_SECTION = "Mania";
    private const string MANIA_KEYS_PROPERTY = "Keys";
    private static readonly string[] ImageFileExtensions = [".png", ".jpg"];

    protected static byte[] TransparentPngFile => new byte[] {
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x04, 0x00, 0x00, 0x00, 0xB5, 0x1C, 0x0C,
        0x02, 0x00, 0x00, 0x00, 0x0B, 0x49, 0x44, 0x41, 0x54, 0x78, 0xDA, 0x63, 0x64, 0x60, 0x00, 0x00,
        0x00, 0x06, 0x00, 0x02, 0x30, 0x81, 0xD0, 0x2F, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44,
        0xAE, 0x42, 0x60, 0x82
    };

    /// <summary>The skin options that will be used to populate the tasks.</summary>
    public SkinOption[] SkinOptions { get; set; }

    /// <summary>Represents the progress of the operation as a value between 0 and 100 or null if there is no ongoing operation..</summary>
    public double? Progress
    {
        get
        {
            return _progress;
        }
        protected set
        {
            if (value == null)
                _progress = null;
            else if (value < 0)
                _progress = 0;
            else if (value > 100)
                _progress = 100;
            else
                _progress = value;

            ProgressChanged?.Invoke(_progress);
        }
    }

    public Action<double?> ProgressChanged { get; set; }

    public Action<string> StatusChanged { get; set; }

    protected virtual bool CacheOriginalElements => false;

    protected Dictionary<string, MemoryStream> OriginalElementsCache { get; } = new();

    protected Dictionary<(OsuSkin skin, string filename), string> Md5Map { get; } = new();

    protected CancellationToken CancellationToken { get; set; }

    protected IEnumerable<SkinOption> FlattenedBottomLevelOptions => SkinOption.Flatten(SkinOptions).Where(o => o is not ParentSkinOption);

    private readonly List<StringBuilder> _logBuilders = new();

    private StringBuilder _currentLogBuilder;

    private readonly List<Action> _tasks = new();

    private readonly Stopwatch _stopwatch = new();

    private double? _progress;

    private bool _disposedValue;

    public void Run(CancellationToken cancellationToken)
    {
        CancellationToken = cancellationToken;
        OriginalElementsCache.Clear();
        _tasks.Clear();
        _logBuilders.Clear();

        Settings.Log("Started skin machine.");
        _stopwatch.Reset();
        _stopwatch.Start();

        try
        {
            Progress = 0;

            PopulateTasks();
            RunAllTasks();

            Progress = 100;

            PostRun();

            Settings.Content.SkinsMadeCount++;
            _stopwatch.Stop();
            Settings.Log($"Finished skin machine in {_stopwatch.Elapsed.TotalSeconds}s");
        }
        catch
        {
            throw;
        }
        finally
        {
            Progress = null;
            Settings.Log("Logs for skin machine follows:");

            _logBuilders.Add(_currentLogBuilder);
            foreach (var builder in _logBuilders)
                Settings.Log(builder.ToString());
        }
    }

    protected abstract void PopulateTasks();

    private void RunAllTasks()
    {
        StatusChanged?.Invoke("Writing changes...");
        double progressInterval = (100.0 - Progress.Value) / _tasks.Count;
        foreach (Action task in _tasks)
        {
            Progress += progressInterval;
            task();
        }
    }

    protected abstract void PostRun();

    protected void GenerateCreditsFile(OsuSkin workingSkin)
    {
        foreach (var pair in Md5Map)
        {
            OsuSkin skin = pair.Key.skin;
            string elementFilename = pair.Key.filename;

            // Avoid duplicate filenames in the credits file, for when we are modifying a skin.
            RemoveCreditIfExists(workingSkin, elementFilename);

            // Check if this element originally came from another skin, and if so, credit that skin instead.
            // if (skin.Credits.TryGetSkinFromElementFilename(elementFilename, out OsuSkinCreditsSkin skinToCredit))
            // {
            //     workingSkin.Credits.AddElement(
            //         skinName: skinToCredit.SkinName,
            //         skinAuthor: skinToCredit.SkinAuthor,
            //         checksum: pair.Value,
            //         filename: elementFilename);

            //     continue;
            // }

            workingSkin.Credits.AddElement(
                skinName: skin.Name,
                skinAuthor: skin.SkinIni?.TryGetPropertyValue("General", "Author"),
                checksum: pair.Value,
                filename: elementFilename);
        }

        string creditsFilePath = workingSkin.WriteCreditsFile();
        AddFileToOriginalElementsCache(creditsFilePath);
    }

    protected static void RemoveCreditIfExists(OsuSkin workingSkin, string elementFilename)
    {
        if (workingSkin.Credits.TryGetSkinFromElementFilename(elementFilename, out OsuSkinCreditsSkin existingCreditedSkin))
        {
            workingSkin.Credits.RemoveElement(
                skinName: existingCreditedSkin.SkinName,
                skinAuthor: existingCreditedSkin.SkinAuthor,
                filename: elementFilename);
        }
    }

    protected static string GetSkinRelativePath(OsuSkin skin, FileInfo file)
        => Path.GetRelativePath(skin.Directory.FullName, file.FullName).Replace('\\', '/');

    public static string GetMd5Hash(string filePath)
    {
        using MD5 md5 = MD5.Create();
        using FileStream stream = File.OpenRead(filePath);

        byte[] hashBytes = md5.ComputeHash(stream);

        StringBuilder sb = new();
        foreach (byte b in hashBytes)
            sb.Append(b.ToString("x2"));

        return sb.ToString();
    }

    protected void CopyOption(OsuSkin workingSkin, SkinOption option)
    {
        StatusChanged?.Invoke($"Copying: {(option as SkinFileOption)?.IncludeFileName ?? option.Name}");
        switch (option)
        {
            case SkinIniPropertyOption iniPropertyOption:
                CopyIniPropertyOption(workingSkin, iniPropertyOption);
                break;
            case SkinIniSectionOption iniSectionOption:
                CopyIniSectionOption(workingSkin, iniSectionOption);
                break;
            case SkinFileOption fileOption:
                CopyFileOption(workingSkin, fileOption);
                break;
            case SkinManiaKeysOption maniaKeysOption:
                CopyManiaKeysOption(workingSkin, maniaKeysOption);
                break;
        }
    }

    protected virtual void CopyIniPropertyOption(OsuSkin workingSkin, SkinIniPropertyOption iniPropertyOption)
    {
        if (iniPropertyOption.Value.Type == SkinOptionValueType.DefaultSkin || iniPropertyOption.Value.CustomSkin?.SkinIni == null)
            return;

        foreach (var section in iniPropertyOption.Value.CustomSkin.SkinIni.Sections)
        {
            if (iniPropertyOption.IncludeSkinIniProperty.section != section.Name)
                continue;

            foreach (var pair in section)
            {
                if (pair.Key == iniPropertyOption.IncludeSkinIniProperty.property)
                {
                    OsuSkinIniSection newSkinSection = workingSkin.SkinIni.Sections.LastOrDefault(s => s.Name == section.Name);
                    if (newSkinSection == null)
                    {
                        newSkinSection = new OsuSkinIniSection(section.Name);
                        workingSkin.SkinIni.Sections.Add(newSkinSection);
                    }

                    AddTask(() =>
                    {
                        Log($"Run task copy skin.ini property '{section.Name}.{pair.Key}: {pair.Value}'");
                        newSkinSection.Add(
                            key: pair.Key,
                            value: pair.Value);
                    });

                    // Check if the skin.ini property value includes any skin elements.
                    // If so, include it in the new skin, (their inclusion takes priority over the elements from matching filenames)
                    CopyFileFromSkinIniProperty(workingSkin, iniPropertyOption.Value.CustomSkin, pair);
                }
            }
        }
    }

    protected virtual void CopyIniSectionOption(OsuSkin workingSkin, SkinIniSectionOption iniSectionOption)
    {
        if (iniSectionOption.Value.Type == SkinOptionValueType.DefaultSkin || iniSectionOption.Value.CustomSkin?.SkinIni == null)
            return;

        OsuSkinIniSection section = iniSectionOption.Value.CustomSkin.SkinIni.Sections.Find(
            s => s.Name == iniSectionOption.SectionName && s.Contains(iniSectionOption.Property));

        if (section == null)
            return;

        Log($"Copying skin.ini section '{iniSectionOption.SectionName}' where '{iniSectionOption.Property.Key}: {iniSectionOption.Property.Value}'");

        workingSkin.SkinIni.Sections.Add(section);
        foreach (var property in section)
            CopyFileFromSkinIniProperty(workingSkin, iniSectionOption.Value.CustomSkin, property);
    }

    protected virtual void CopyFileFromSkinIniProperty(OsuSkin workingSkin, OsuSkin skinToCopy, KeyValuePair<string, string> property)
    {
        if (!OsuSkinIni.PropertyHasFilePath(property.Key))
            return;

        int lastSlashIndex = property.Value.LastIndexOf('/');
        string prefixPropertyDirPath = lastSlashIndex >= 0 ? property.Value[..lastSlashIndex] : null;
        string prefixPropertyFileName = property.Value[(lastSlashIndex + 1)..];

        // If `prefixPropertyDirPath` is null, the path is the skin folder root which obviously exists.
        if (prefixPropertyDirPath != null && !Directory.Exists($"{skinToCopy.Directory.FullName}/{prefixPropertyDirPath}"))
            return;

        // In that case, better to use the existing file collection that we have instead of creating another one.
        IEnumerable<FileInfo> files = prefixPropertyDirPath == null ?
            skinToCopy.Directory.EnumerateFiles() : new DirectoryInfo($"{skinToCopy.Directory.FullName}/{prefixPropertyDirPath}").EnumerateFiles();

        var fileDestDir = Directory.CreateDirectory($"{workingSkin.Directory.FullName}/{prefixPropertyDirPath}");
        foreach (var file in files)
        {
            if (file.Name.StartsWith(prefixPropertyFileName, StringComparison.OrdinalIgnoreCase))
            {
                Md5Map[(skinToCopy, file.Name)] = GetMd5Hash(file.FullName);
                AddCopyFileTask(file, fileDestDir, "due to skin.ini");
            }
        }
    }

    protected virtual void CopyFileOption(OsuSkin workingSkin, SkinFileOption fileOption)
    {
        if (fileOption.Value.Type == SkinOptionValueType.DefaultSkin)
            return;

        if (fileOption.Value.Type == SkinOptionValueType.Blank)
        {
            AddCopyBlankFileTask(fileOption, workingSkin);
            return;
        }

        foreach (var file in fileOption.Value.CustomSkin.Directory.EnumerateFiles())
        {
            if (CheckIfFileAndOptionMatch(file, fileOption))
            {
                AddCopyFileTask(file, workingSkin.Directory, "due to filename match");
                Md5Map[(fileOption.Value.CustomSkin, file.Name)] = GetMd5Hash(file.FullName);
            }
        }
    }

    protected virtual void CopyManiaKeysOption(OsuSkin workingSkin, SkinManiaKeysOption maniaKeysOption)
    {
        if (maniaKeysOption.Value.Type == SkinOptionValueType.DefaultSkin)
            return;

        OsuSkin sourceSkin = maniaKeysOption.Value.CustomSkin;
        if (sourceSkin?.Directory == null)
            return;

        int keys = maniaKeysOption.Keys;
        OsuSkinIniSection sourceSection = FindManiaSection(sourceSkin, keys);
        OsuSkinIniSection newSection = sourceSection != null
            ? CloneIniSection(sourceSection)
            : new OsuSkinIniSection(MANIA_SKIN_INI_SECTION);

        newSection[MANIA_KEYS_PROPERTY] = keys.ToString();

        Log($"Copying osu!mania {keys}K section from '{sourceSkin.Name}'");

        bool copiedAnyFiles = false;
        Dictionary<string, bool> copiedPrefixes = new(StringComparer.OrdinalIgnoreCase);
        foreach (var imageProperty in GetManiaImageProperties(keys, newSection))
        {
            bool hasCustomValue = newSection.TryGetValue(imageProperty.Name, out string propertyValue);
            string sourcePrefix = hasCustomValue
                ? NormalizeSkinElementPrefix(propertyValue)
                : imageProperty.DefaultPrefix;

            if (string.IsNullOrWhiteSpace(sourcePrefix))
                continue;

            string destinationPrefix = GetManiaDestinationPrefix(sourceSkin, sourcePrefix);
            string copyCacheKey = $"{sourcePrefix}\0{destinationPrefix}";
            if (!copiedPrefixes.TryGetValue(copyCacheKey, out bool copied))
            {
                copied = CopyImagePrefixToPreservedPath(
                    sourceSkin,
                    workingSkin,
                    sourcePrefix,
                    destinationPrefix,
                    $"due to osu!mania {keys}K {imageProperty.Name}");

                copiedPrefixes[copyCacheKey] = copied;
            }

            if (copied)
            {
                newSection[imageProperty.Name] = destinationPrefix;
                copiedAnyFiles = true;
            }
        }

        if (sourceSection != null || copiedAnyFiles)
            workingSkin.SkinIni.Sections.Add(newSection);
    }

    protected void AddTask(Action task)
        => _tasks.Add(task);

    protected void AddPriorityTask(Action task)
        => _tasks.Insert(0, task);

    protected void AddCopyFileTask(FileInfo file, DirectoryInfo fileDestDir, string reason)
        => AddCopyFileTask(file, fileDestDir, file.Name, reason);

    protected void AddCopyFileTask(FileInfo file, DirectoryInfo fileDestDir, string destFileName, string reason)
    {
        string destFullPath = Path.Combine(fileDestDir.FullName, destFileName);

        // We cache the file data beforehand in case it changes or is deleted before we have the chance to copy it.
        MemoryStream memoryStream = new();
        file.OpenRead().CopyTo(memoryStream);

        AddFileToOriginalElementsCache(destFullPath);

        _tasks.Add(() =>
        {
            Log($"Run task '{file.FullName}' -> '{destFullPath}' ({reason})");

            using FileStream fileStream = File.Create(destFullPath);
            memoryStream.Position = 0;
            memoryStream.CopyTo(fileStream);

            if (!CacheOriginalElements)
                memoryStream.Dispose();
        });
    }

    protected void AddCopyBlankFileTask(SkinFileOption fileOption, OsuSkin workingSkin)
    {
        if (fileOption.IncludeFileName.EndsWith("*"))
        {
            string filenameWithoutWildcard = fileOption.IncludeFileName.Replace("*", string.Empty);

            if (fileOption.AllowedSuffixes == null)
            {
                add(filenameWithoutWildcard);
                return;
            }

            foreach (var suffix in fileOption.AllowedSuffixes)
                add($"{filenameWithoutWildcard}{suffix}");

            return;
        }

        add(fileOption.IncludeFileName);

        void add(string filename)
        {
            string destPathWithoutExtension = Path.Combine(workingSkin.Directory.FullName, filename);

            if (fileOption.IsAudio)
            {
                string wavDestPath = $"{destPathWithoutExtension}.wav";
                AddFileToOriginalElementsCache(wavDestPath);
                _tasks.Add(() =>
                {
                    Log($"Run task (blank file) -> '{wavDestPath}'");
                    File.Create(wavDestPath).Dispose();
                });
            }
            else
            {
                string pngDestPath = $"{destPathWithoutExtension}.png";
                string pngDestPath2x = $"{destPathWithoutExtension}@2x.png";

                AddFileToOriginalElementsCache(pngDestPath);
                AddFileToOriginalElementsCache(pngDestPath2x);

                Settings.Log($"dbg: {pngDestPath}");
                RemoveCreditIfExists(workingSkin, Path.GetFileName(pngDestPath));
                RemoveCreditIfExists(workingSkin, Path.GetFileName(pngDestPath2x));

                _tasks.Add(() =>
                {
                    Log($"Run task (blank file) -> '{pngDestPath}'");

                    // This is a 1x1 transparent PNG file. A zero byte file will cause osu! to fall back to the default skin.
                    File.WriteAllBytes(pngDestPath, TransparentPngFile);
                    File.WriteAllBytes(pngDestPath2x, TransparentPngFile);
                });
            }
        }
    }

    protected static bool IsManiaSectionForKeys(OsuSkinIniSection section, int keys)
    {
        if (!section.Name.Equals(MANIA_SKIN_INI_SECTION, StringComparison.OrdinalIgnoreCase))
            return false;

        return section.TryGetValue(MANIA_KEYS_PROPERTY, out string value)
            && int.TryParse(value, out int sectionKeys)
            && sectionKeys == keys;
    }

    protected void AddFileToOriginalElementsCache(string fullFilePath)
    {
        if (!CacheOriginalElements)
            return;

        // Cache original element for when after creation has finished, in case an undo operation is requested.
        MemoryStream originalMemoryStream = null;
        if (File.Exists(fullFilePath))
        {
            FileStream originalFileStream = File.OpenRead(fullFilePath);
            originalMemoryStream = new MemoryStream();
            originalFileStream.CopyTo(originalMemoryStream);
            originalFileStream.Dispose();
        }

        OriginalElementsCache.TryAdd(fullFilePath, originalMemoryStream);
    }

    protected void Log(string message)
    {
        _currentLogBuilder ??= new StringBuilder() { Capacity = LOG_SPLIT_CHAR_SIZE };

        _currentLogBuilder
            .AppendLine()
            .Append(_stopwatch.ElapsedMilliseconds)
            .Append("ms: ")
            .Append(message);

        // Funky stuff happens if we print a massive string, so split it.
        // We don't print the logs as soon as they arrive as we print a lot of logs at once,
        // and Godot flushes the output buffer on program exit, causing a freeze.
        if (_currentLogBuilder.Length > LOG_SPLIT_CHAR_SIZE)
        {
            _logBuilders.Add(_currentLogBuilder);
            _currentLogBuilder = null;
        }
    }

    protected static bool CheckIfFileAndOptionMatch(FileInfo file, SkinFileOption fileOption)
    {
        string filename = Path.GetFileNameWithoutExtension(file.Name);
        string extension = Path.GetExtension(file.Name);

        // Check for file name match.
        if (
            filename.Equals(fileOption.IncludeFileName, StringComparison.OrdinalIgnoreCase)
            || filename.Equals(fileOption.IncludeFileName + "@2x", StringComparison.OrdinalIgnoreCase)
            || (
                filename.StartsWith(fileOption.IncludeFileName.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)
                && (
                    CheckIfFileNameIsAnimation(filename, fileOption)
                    || CheckIfWildcardMatchesFile(filename, fileOption)
                )
            )
        )
        {
            // Check for file type match.
            if (
                ((extension == ".png" || extension == ".jpg") && !fileOption.IsAudio)
                || ((extension == ".mp3" || extension == ".ogg" || extension == ".wav") && fileOption.IsAudio)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static bool CheckIfFileNameIsAnimation(string filename, SkinFileOption fileOption)
    {
        if (!fileOption.IsAnimatable)
            return false;

        // An file representing an animation frame would have a number suffix e.g. menu-back-10.png or sliderb10.png.
        string filenameSuffix = filename.ToLower().TrimPrefix(fileOption.IncludeFileName.ToLower());
        return int.TryParse(filenameSuffix, out int _) || int.TryParse(filenameSuffix.TrimSuffix("@2x"), out int _);
    }

    private static bool CheckIfWildcardMatchesFile(string filename, SkinFileOption fileOption)
    {
        if (!fileOption.IncludeFileName.EndsWith("*"))
            return false;

        if (fileOption.AllowedSuffixes == null)
            return true;

        string filenameSuffix = filename.ToLower().TrimSuffix("@2x").TrimPrefix(fileOption.IncludeFileName.ToLower().TrimEnd('*'));
        return fileOption.AllowedSuffixes.Any(s => filenameSuffix.Equals(s, StringComparison.OrdinalIgnoreCase));
    }

    private static OsuSkinIniSection FindManiaSection(OsuSkin skin, int keys)
        => skin.SkinIni?.Sections.FirstOrDefault(section => IsManiaSectionForKeys(section, keys));

    private static OsuSkinIniSection CloneIniSection(OsuSkinIniSection section)
    {
        OsuSkinIniSection result = new(section.Name);
        foreach (var pair in section)
            result[pair.Key] = pair.Value;

        return result;
    }

    protected static IEnumerable<string> GetSkinIniFilePrefixes(OsuSkinIniSection section)
        => section
            .Where(pair => OsuSkinIni.PropertyHasFilePath(pair.Key))
            .Select(pair => NormalizeSkinElementPrefix(pair.Value))
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix));

    protected static FileInfo[] GetImageFilesForPrefix(DirectoryInfo skinDir, string prefix)
    {
        string normalizedPrefix = NormalizeSkinElementPrefix(prefix);
        int lastSlashIndex = normalizedPrefix.LastIndexOf('/');
        string dirRelativePath = lastSlashIndex >= 0 ? normalizedPrefix[..lastSlashIndex] : null;
        string filePrefix = normalizedPrefix[(lastSlashIndex + 1)..];

        DirectoryInfo dir = dirRelativePath == null
            ? skinDir
            : new DirectoryInfo(Path.Combine(skinDir.FullName, dirRelativePath));

        if (!dir.Exists)
            return Array.Empty<FileInfo>();

        return dir
            .EnumerateFiles()
            .Where(file => IsImagePrefixMatch(file, filePrefix))
            .ToArray();
    }

    private bool CopyImagePrefixToPreservedPath(OsuSkin sourceSkin, OsuSkin workingSkin, string sourcePrefix, string destinationPrefix, string reason)
    {
        string normalizedSourcePrefix = NormalizeSkinElementPrefix(sourcePrefix);
        string normalizedDestinationPrefix = NormalizeSkinElementPrefix(destinationPrefix);
        int lastSlashIndex = normalizedDestinationPrefix.LastIndexOf('/');
        string destinationDirRelativePath = lastSlashIndex >= 0 ? normalizedDestinationPrefix[..lastSlashIndex] : null;
        string destinationFilePrefix = normalizedDestinationPrefix[(lastSlashIndex + 1)..];

        DirectoryInfo destinationDir = destinationDirRelativePath == null
            ? workingSkin.Directory
            : Directory.CreateDirectory(Path.Combine(workingSkin.Directory.FullName, destinationDirRelativePath));

        bool copied = false;
        foreach (FileInfo file in GetImageFilesForPrefix(sourceSkin.Directory, normalizedSourcePrefix))
        {
            string filenameWithoutExtension = Path.GetFileNameWithoutExtension(file.Name);
            string sourceFilePrefix = Path.GetFileName(normalizedSourcePrefix);
            string suffix = filenameWithoutExtension[sourceFilePrefix.Length..];
            string destinationFileName = $"{destinationFilePrefix}{suffix}{file.Extension}";
            string destinationRelativeFileName = destinationDirRelativePath == null
                ? destinationFileName
                : $"{destinationDirRelativePath}/{destinationFileName}";

            Md5Map[(sourceSkin, destinationRelativeFileName)] = GetMd5Hash(file.FullName);
            AddCopyFileTask(file, destinationDir, destinationFileName, reason);
            copied = true;
        }

        return copied;
    }

    protected static bool IsImagePrefixMatch(FileInfo file, string prefix)
    {
        if (!ImageFileExtensions.Any(extension => file.Extension.Equals(extension, StringComparison.OrdinalIgnoreCase)))
            return false;

        string filenameWithoutExtension = Path.GetFileNameWithoutExtension(file.Name);
        if (!filenameWithoutExtension.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        string suffix = filenameWithoutExtension[prefix.Length..];
        return suffix.Length == 0
            || suffix.Equals("@2x", StringComparison.OrdinalIgnoreCase)
            || suffix.StartsWith('-');
    }

    protected static string NormalizeSkinElementPrefix(string value)
    {
        string normalized = value?.Trim().Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
            return normalized;

        string extension = Path.GetExtension(normalized);
        if (ImageFileExtensions.Any(imageExtension => extension.Equals(imageExtension, StringComparison.OrdinalIgnoreCase)))
            normalized = normalized[..^extension.Length];

        return normalized;
    }

    private static string GetManiaDestinationPrefix(OsuSkin sourceSkin, string sourcePrefix)
        => $"{GetSafeSkinDirectoryName(sourceSkin)}/{NormalizeSkinElementPrefix(sourcePrefix)}";

    private static string GetSafeSkinDirectoryName(OsuSkin skin)
    {
        string name = skin.Directory?.Name ?? skin.Name ?? "skin";
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
            name = name.Replace(invalidChar, '_');

        name = name.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? "skin" : name;
    }

    private static IEnumerable<ManiaImageProperty> GetManiaImageProperties(int keys, OsuSkinIniSection section)
    {
        ManiaSpecialStyle specialStyle = GetManiaSpecialStyle(section);

        for (int i = 0; i < keys; i++)
        {
            string columnSuffix = GetManiaColumnSkinSuffix(keys, specialStyle, i);

            yield return new($"KeyImage{i}", $"mania-key{columnSuffix}");
            yield return new($"KeyImage{i}D", $"mania-key{columnSuffix}D");
            yield return new($"NoteImage{i}", $"mania-note{columnSuffix}");
            yield return new($"NoteImage{i}H", $"mania-note{columnSuffix}H");
            yield return new($"NoteImage{i}L", $"mania-note{columnSuffix}L");
            yield return new($"NoteImage{i}T", $"mania-note{columnSuffix}T");
        }

        yield return new("StageLeft", "mania-stage-left");
        yield return new("StageRight", "mania-stage-right");
        yield return new("StageBottom", "mania-stage-bottom");
        yield return new("StageHint", "mania-stage-hint");
        yield return new("StageLight", "mania-stage-light");
        yield return new("Hit0", "mania-hit0");
        yield return new("Hit50", "mania-hit50");
        yield return new("Hit100", "mania-hit100");
        yield return new("Hit200", "mania-hit200");
        yield return new("Hit300", "mania-hit300");
        yield return new("Hit300g", "mania-hit300g");
        yield return new("LightingN", "lightingN");
        yield return new("LightingL", "lightingL");
        yield return new("WarningArrow", "mania-warningarrow");
    }

    private static ManiaSpecialStyle GetManiaSpecialStyle(OsuSkinIniSection section)
    {
        if (!section.TryGetValue("SpecialStyle", out string value))
            return ManiaSpecialStyle.None;

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "left" => ManiaSpecialStyle.Left,
            "2" or "right" => ManiaSpecialStyle.Right,
            _ => ManiaSpecialStyle.None,
        };
    }

    private static string GetManiaColumnSkinSuffix(int keys, ManiaSpecialStyle specialStyle, int column)
    {
        bool useSpecialStyle = keys > 4 && keys % 2 == 0 && specialStyle != ManiaSpecialStyle.None;
        int specialColumn = specialStyle == ManiaSpecialStyle.Right ? keys - 1 : 0;

        if (useSpecialStyle)
        {
            if (column == specialColumn)
                return "S";

            if ((column % 2 == 1 && specialStyle == ManiaSpecialStyle.Right)
                || (column % 2 == 0 && specialStyle == ManiaSpecialStyle.Left))
                return "2";

            return "1";
        }

        int middleColumn = (int)Math.Floor(keys / 2f);
        if (keys % 2 == 1 && keys > 4 && column == middleColumn)
            return "S";

        bool isNormalKey2 =
            (keys % 2 == 1 && column != middleColumn && column % 2 == 1)
            || (keys % 2 == 0 && ((column < middleColumn && column % 2 == 1) || (column >= middleColumn && column % 2 == 0)));

        return isNormalKey2 ? "2" : "1";
    }

    private readonly record struct ManiaImageProperty(string Name, string DefaultPrefix);

    private enum ManiaSpecialStyle
    {
        None,
        Left,
        Right,
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                foreach (var pair in OriginalElementsCache)
                    pair.Value.Dispose();
            }

            _tasks.Clear();
            _logBuilders.Clear();
            _disposedValue = true;
        }
    }
}
