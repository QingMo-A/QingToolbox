using System.IO;
using System.Globalization;
using System.Text.Json;

namespace QingToolbox.Modules.Launcher;

/// <summary>Small module-owned atomic JSON store. Targets stay backend-only.</summary>
public sealed class LauncherStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private List<LauncherItem> _items = [];
    private List<LauncherItem> _desktopItems = [];
    private string _sortMode = "custom";
    private LauncherHotkeySpec _hotkey = LauncherHotkeySpec.Default;

    public LauncherStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "launcher.json");
        Load();
    }

    public string SortMode { get { lock (_gate) return _sortMode; } }
    public LauncherHotkeySpec Hotkey { get { lock (_gate) return _hotkey; } }
    internal IReadOnlyList<LauncherItem> Items { get { lock (_gate) return _items.ToArray(); } }
    internal IReadOnlyList<LauncherItem> DesktopItems { get { lock (_gate) return _desktopItems.ToArray(); } }

    public LauncherStateView Snapshot(string hotkeyStatus = "Inactive", bool active = false)
    {
        lock (_gate)
        {
            var projected = ProjectItemsLocked();
            var recent = _items.Concat(_desktopItems).Where(item => item.LastLaunchedAt is not null)
                .OrderByDescending(item => item.LastLaunchedAt)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .GroupBy(item => NormalizePath(item.Target), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(10)
                .Select(item => ToView(item, _desktopItems.Any(value => value.Id == item.Id) ? "desktop" : "custom"))
                .ToArray();
            return new(
                _sortMode,
                projected.Select(item => ToView(item, _sortMode == "desktop" ? "desktop" : "custom")).ToArray(),
                recent,
                new(_hotkey.Ctrl, _hotkey.Alt, _hotkey.Shift, _hotkey.Win, _hotkey.VirtualKey, _hotkey.KeyLabel),
                hotkeyStatus,
                active);
        }
    }

    public bool TryGet(string id, out LauncherItem item)
    {
        lock (_gate)
        {
            item = _items.Concat(_desktopItems).FirstOrDefault(value => string.Equals(value.Id, id, StringComparison.Ordinal))!;
            return item is not null;
        }
    }

    public bool Add(ResolvedLauncherItem resolved, string? iconKey, out LauncherItem item)
    {
        lock (_gate)
        {
            var target = NormalizePath(resolved.Target);
            var existing = _items.FirstOrDefault(value =>
                string.Equals(NormalizePath(value.Target), target, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                item = existing;
                return false;
            }

            item = new LauncherItem(
                Guid.NewGuid().ToString("N"),
                string.IsNullOrWhiteSpace(resolved.Name) ? Path.GetFileNameWithoutExtension(target) : resolved.Name.Trim(),
                target,
                resolved.Arguments ?? string.Empty,
                NormalizePath(string.IsNullOrWhiteSpace(resolved.WorkingDirectory) ? Path.GetDirectoryName(target)! : resolved.WorkingDirectory),
                iconKey,
                null,
                NormalizePath(string.IsNullOrWhiteSpace(resolved.IconSourcePath) ? target : resolved.IconSourcePath),
                NormalizePath(string.IsNullOrWhiteSpace(resolved.OriginPath) ? target : resolved.OriginPath));
            _items.Add(item);
            SaveLocked();
            return true;
        }
    }

    public bool Remove(string id, out LauncherItem? removed)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (index < 0) { removed = null; return false; }
            removed = _items[index];
            _items.RemoveAt(index);
            SaveLocked();
            return true;
        }
    }

    public bool SetSortMode(string mode)
    {
        if (mode is not ("custom" or "alphabetical" or "desktop")) return false;
        lock (_gate)
        {
            if (_sortMode == mode) return true;
            _sortMode = mode;
            SaveLocked();
            return true;
        }
    }

    public bool SetCustomOrder(IReadOnlyList<string> ids)
    {
        lock (_gate)
        {
            var target = _sortMode == "desktop" ? _desktopItems : _items;
            if (_sortMode == "alphabetical" || ids.Count != target.Count || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) return false;
            var known = target.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            if (ids.Any(id => !known.Contains(id))) return false;
            var byId = target.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var ordered = ids.Select(id => byId[id]).ToList();
            if (_sortMode == "desktop") _desktopItems = ordered;
            else _items = ordered;
            SaveLocked();
            return true;
        }
    }

    public void SynchronizeDesktopItems(IReadOnlyList<ResolvedLauncherItem> resolvedItems)
    {
        lock (_gate)
        {
            var existing = _desktopItems.GroupBy(DesktopIdentity, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var discovered = resolvedItems
                .GroupBy(DesktopIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToDictionary(DesktopIdentity, StringComparer.OrdinalIgnoreCase);
            var next = _desktopItems.Where(item => discovered.ContainsKey(DesktopIdentity(item)))
                .Select(item => RefreshDesktopItem(item, discovered[DesktopIdentity(item)]))
                .ToList();
            foreach (var resolved in resolvedItems)
            {
                var identity = DesktopIdentity(resolved);
                if (next.Any(item => string.Equals(DesktopIdentity(item), identity, StringComparison.OrdinalIgnoreCase))) continue;
                if (existing.TryGetValue(identity, out var retained)) { next.Add(retained); continue; }
                next.Add(CreateItem(resolved));
            }
            _desktopItems = next;
            SaveLocked();
        }
    }

    public bool SetHotkey(LauncherHotkeySpec hotkey)
    {
        if (!hotkey.IsValid) return false;
        lock (_gate)
        {
            _hotkey = hotkey.Normalize();
            SaveLocked();
            return true;
        }
    }

    public bool MarkLaunched(string id, DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            var list = _items.Any(item => item.Id == id) ? _items : _desktopItems;
            var index = list.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (index < 0) return false;
            list[index] = list[index] with { LastLaunchedAt = timestamp };
            SaveLocked();
            return true;
        }
    }

    public bool SetIconKey(string id, string? iconKey)
    {
        lock (_gate)
        {
            var list = _items.Any(item => item.Id == id) ? _items : _desktopItems;
            var index = list.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (index < 0) return false;
            list[index] = list[index] with { IconKey = iconKey };
            SaveLocked();
            return true;
        }
    }

    private IReadOnlyList<LauncherItem> ProjectItemsLocked()
    {
        if (_sortMode == "desktop") return _desktopItems.ToArray();
        if (_sortMode != "alphabetical") return _items.ToArray();
        return _items.Select((item, index) => (item, index))
            .OrderBy(pair => pair.item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.item)
            .ToArray();
    }

    private void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                var document = JsonSerializer.Deserialize<LauncherStoreDocument>(File.ReadAllText(_path), JsonOptions);
                if (document is null) return;
                _sortMode = document.SortMode is "alphabetical" or "desktop" ? document.SortMode : "custom";
                _hotkey = (document.Hotkey ?? LauncherHotkeySpec.Default).Normalize();
                _items = (document.Items ?? [])
                    .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Target))
                    .GroupBy(item => item.Id, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .Select(item => item with
                    {
                        Id = item.Id.Trim(),
                        Name = string.IsNullOrWhiteSpace(item.Name) ? Path.GetFileNameWithoutExtension(item.Target) : item.Name.Trim(),
                        Target = NormalizePath(item.Target),
                        Arguments = item.Arguments ?? string.Empty,
                        WorkingDirectory = NormalizePath(string.IsNullOrWhiteSpace(item.WorkingDirectory)
                            ? Path.GetDirectoryName(item.Target) ?? string.Empty
                            : item.WorkingDirectory),
                        IconSourcePath = string.IsNullOrWhiteSpace(item.IconSourcePath)
                            ? null
                            : NormalizePath(item.IconSourcePath),
                        OriginPath = string.IsNullOrWhiteSpace(item.OriginPath)
                            ? NormalizePath(item.Target)
                            : NormalizePath(item.OriginPath),
                    })
                    .ToList();
                _desktopItems = NormalizeItems(document.DesktopItems ?? []);
            }
            catch (JsonException) { ResetLocked(); }
            catch (IOException) { ResetLocked(); }
            catch (UnauthorizedAccessException) { ResetLocked(); }
            catch (ArgumentException) { ResetLocked(); }
        }
    }

    private void ResetLocked()
    {
        _items = [];
        _desktopItems = [];
        _sortMode = "custom";
        _hotkey = LauncherHotkeySpec.Default;
    }

    private void SaveLocked()
    {
        var temporary = $"{_path}.tmp.{Guid.NewGuid():N}";
        try
        {
            var document = new LauncherStoreDocument { SortMode = _sortMode, Hotkey = _hotkey, Items = _items, DesktopItems = _desktopItems };
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            if (File.Exists(_path)) File.Replace(temporary, _path, null, true);
            else File.Move(temporary, _path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string NormalizePath(string value)
    {
        try { return Path.GetFullPath(value); }
        catch (ArgumentException) { return value.Trim(); }
        catch (NotSupportedException) { return value.Trim(); }
    }

    private static LauncherItem CreateItem(ResolvedLauncherItem resolved)
    {
        var target = NormalizePath(resolved.Target);
        return new(Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(resolved.Name) ? Path.GetFileNameWithoutExtension(target) : resolved.Name.Trim(),
            target, resolved.Arguments ?? string.Empty,
            NormalizePath(string.IsNullOrWhiteSpace(resolved.WorkingDirectory) ? Path.GetDirectoryName(target)! : resolved.WorkingDirectory),
            null, null, NormalizePath(string.IsNullOrWhiteSpace(resolved.IconSourcePath) ? target : resolved.IconSourcePath),
            NormalizePath(string.IsNullOrWhiteSpace(resolved.OriginPath) ? target : resolved.OriginPath));
    }

    private static List<LauncherItem> NormalizeItems(IEnumerable<LauncherItem> items) => items
        .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Target))
        .GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => group.First())
        .Select(item => item with { Id = item.Id.Trim(), Name = string.IsNullOrWhiteSpace(item.Name) ? Path.GetFileNameWithoutExtension(item.Target) : item.Name.Trim(), Target = NormalizePath(item.Target), Arguments = item.Arguments ?? string.Empty, WorkingDirectory = NormalizePath(string.IsNullOrWhiteSpace(item.WorkingDirectory) ? Path.GetDirectoryName(item.Target) ?? string.Empty : item.WorkingDirectory), IconSourcePath = string.IsNullOrWhiteSpace(item.IconSourcePath) ? null : NormalizePath(item.IconSourcePath), OriginPath = string.IsNullOrWhiteSpace(item.OriginPath) ? NormalizePath(item.Target) : NormalizePath(item.OriginPath) }).ToList();

    private static string DesktopIdentity(ResolvedLauncherItem item) => NormalizePath(item.OriginPath ?? item.Target);
    private static string DesktopIdentity(LauncherItem item) => NormalizePath(item.OriginPath ?? item.Target);

    private static LauncherItem RefreshDesktopItem(LauncherItem existing, ResolvedLauncherItem resolved)
    {
        var target = NormalizePath(resolved.Target);
        var iconSource = NormalizePath(string.IsNullOrWhiteSpace(resolved.IconSourcePath) ? target : resolved.IconSourcePath);
        return existing with
        {
            Name = string.IsNullOrWhiteSpace(resolved.Name) ? Path.GetFileNameWithoutExtension(target) : resolved.Name.Trim(),
            Target = target,
            Arguments = resolved.Arguments ?? string.Empty,
            WorkingDirectory = NormalizePath(string.IsNullOrWhiteSpace(resolved.WorkingDirectory) ? Path.GetDirectoryName(target)! : resolved.WorkingDirectory),
            IconKey = string.Equals(existing.IconSourcePath, iconSource, StringComparison.OrdinalIgnoreCase) ? existing.IconKey : null,
            IconSourcePath = iconSource,
            OriginPath = NormalizePath(resolved.OriginPath ?? target),
        };
    }

    private static LauncherItemView ToView(LauncherItem item, string source) =>
        new(item.Id, item.Name, item.IconKey, item.LastLaunchedAt, source);
}
