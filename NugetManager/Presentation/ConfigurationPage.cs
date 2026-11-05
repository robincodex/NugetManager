using System.Diagnostics;
using System.Xml;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using NugetManager.Packages;

namespace NugetManager.Presentation;

internal sealed class ConfigurationPage(Window owner, Func<UiMessage, string> format, Func<bool> canEdit,
    Func<Task> saved, Action changed)
{
    private readonly DockPanel _content = new();
    private readonly ComboBox _files = new();
    private readonly Button _open = new();
    private readonly StackPanel _rows = new();
    private readonly Grid _toolbar = new Grid().Columns("*,Auto,Auto,Auto").Margin(12, 12, 12, 8).DockTop();
    private readonly ScrollViewer _scroll = new ScrollViewer().VerticalScroll(ScrollMode.Auto).HorizontalScroll(ScrollMode.Auto);
    private readonly List<ConfigSnapshot> _snapshots = [];
    private readonly List<UiMessage> _errors = [];
    private IReadOnlyList<string> _paths = [];
    private string? _directory;
    private string? _selectedFile;
    private string? _selectedRowFile;
    private string? _selectedRowName;
    private bool _syncing;
    private bool _loaded;
    private bool _editing;
    private bool _eventsBound;

    public Element Content => _content;
    public bool IsSaving { get; private set; }
    public bool IsEditing => _editing;
    private string L(TextKey key, params string[] arguments) => format(new(key, arguments));
    private bool Editable => canEdit() && !IsSaving && !_editing;

    public void Update(string? directory, bool reload = false)
    {
        if (_directory != directory) { _directory = directory; _loaded = false; _selectedFile = null; }
        if (!_loaded || reload)
        {
            _snapshots.Clear();
            _errors.Clear();
            _selectedRowFile = _selectedRowName = null;
            try
            {
                _paths = directory is null ? [] : NuGetConfigReader.DiscoverFiles(directory)
                    .Append(Path.Combine(directory, "NuGet.Config")).Append(NuGetConfigReader.DefaultUserConfigPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var path in _paths)
                {
                    try { _snapshots.Add(NuGetConfiguration.Read(path)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException
                        or InvalidDataException or InvalidOperationException or ArgumentException)
                    {
                        _errors.Add(new(TextKey.ConfigReadFailed, path, format(UiMessage.FromException(ex))));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _errors.Add(new(TextKey.ConfigReadFailed, directory ?? "", format(UiMessage.FromException(ex))));
                _paths = [];
            }
            _loaded = true;
        }
        Render();
    }

    private void Render()
    {
        _syncing = true;
        _content.Clear();
        _toolbar.Clear();
        _scroll.Content = null;
        _rows.Clear();
        _files.Items(new[] { L(TextKey.ConfigAll) }.Concat(_paths).ToArray());
        _files.SelectedIndex = _selectedFile is null ? 0 : Math.Max(0, _paths.ToList().IndexOf(_selectedFile) + 1);
        _files.IsEnabled = !IsSaving && !_editing;
        if (!_eventsBound)
        {
            _files.OnSelectionChanged(_ =>
            {
                if (_syncing) return;
                _selectedFile = _files.SelectedIndex > 0 ? _paths[_files.SelectedIndex - 1] : null;
                _selectedRowFile = _selectedRowName = null;
                Render();
            });
            _open.OnClick(() => Run(OpenAsync));
            _eventsBound = true;
        }
        _open.Content(L(TextKey.ConfigOpen));
        UpdateOpen();
        var refresh = new Button().Content(L(TextKey.Refresh)).IsEnabled(!IsSaving && !_editing)
            .OnClick(() => Update(_directory, reload: true));
        var add = new Button().Content(L(TextKey.ConfigAdd)).IsEnabled(Editable && _directory is not null)
            .OnClick(() => Run(() => EditAsync(null, null)));
        _content.Add(_toolbar.Children(
            _files.Margin(0, 0, 8, 0),
            _open.Column(1).Margin(0, 0, 8, 0), refresh.Column(2).Margin(0, 0, 8, 0), add.Column(3)));
        if (_directory is null) _rows.Add(Text(L(TextKey.ConfigNoWorkspace)));
        else
        {
            _rows.Add(TableHeader());
            foreach (var snapshot in _snapshots.Where(s => _selectedFile is null || s.Path == _selectedFile))
                foreach (var source in snapshot.Sources)
                    _rows.Add(SourceRow(snapshot, source));
            if (!_snapshots.Where(s => _selectedFile is null || s.Path == _selectedFile).Any(s => s.Sources.Count > 0))
                _rows.Add(Text(L(TextKey.ConfigNoSources)).Margin(8));
        }
        foreach (var error in _errors) _rows.Add(Text(format(error)).Foreground(AppTheme.Current.Error).Margin(8));
        _content.Add(_scroll.Content(_rows.Margin(12, 0, 12, 12)));
        _syncing = false;
    }

    private static Grid TableGrid() => new Grid().Columns("180,320,115,80,260,40,40").MinWidth(1035);
    private Element TableHeader()
    {
        var grid = TableGrid();
        var labels = new[] { TextKey.ConfigName, TextKey.ConfigAddress, TextKey.ConfigAllowInsecure,
            TextKey.ConfigEnabled, TextKey.ConfigFile };
        for (var i = 0; i < labels.Length; i++)
            grid.Add(Text(L(labels[i])).Bold().Column(i).Margin(6));
        return new Border().Background(AppTheme.Current.Header).Child(grid);
    }

    private Element SourceRow(ConfigSnapshot snapshot, ConfigSource source)
    {
        var enabled = Editable && !snapshot.IsReadOnly;
        var insecure = new CheckBox().IsChecked(source.AllowInsecure).Center()
            .IsEnabled(enabled).OnCheckedChanged(value => Run(() => SaveAsync(snapshot, source,
                source with { AllowInsecure = value == true }, null, false)));
        var active = new CheckBox().IsChecked(source.Enabled).Center()
            .IsEnabled(enabled).OnCheckedChanged(value => Run(() => SaveAsync(snapshot, source,
                source with { Enabled = value == true }, null, false)));
        var edit = IconButton(GlyphKind.ChevronRight, TextKey.ConfigEdit, enabled, () => EditAsync(snapshot, source));
        var delete = IconButton(GlyphKind.Minus, TextKey.ConfigDelete, enabled, () => DeleteAsync(snapshot, source));
        var grid = TableGrid().Children(
            Cell(source.Name).Column(0), Cell(source.Location).Column(1), insecure.Column(2), active.Column(3),
            Cell(snapshot.Path + (snapshot.IsReadOnly ? " (" + L(TextKey.ConfigReadOnly) + ")" : "")).Column(4),
            edit.Column(5), delete.Column(6));
        var border = new Border().BorderBrush(AppTheme.Current.Border).BorderThickness(new Thickness(0, 0, 0, 1))
            .Background(_selectedRowFile == snapshot.Path && _selectedRowName == source.Name ? AppTheme.Current.Selected : AppTheme.Current.Row)
            .Child(grid);
        border.OnMouseDown(_ =>
        {
            _selectedRowFile = snapshot.Path;
            _selectedRowName = source.Name;
            foreach (var row in _rows.Children.OfType<Border>().Skip(1)) row.Background = AppTheme.Current.Row;
            border.Background = AppTheme.Current.Selected;
            UpdateOpen();
        });
        return border;
    }

    private void UpdateOpen() =>
        _open.IsEnabled = File.Exists(_selectedFile ?? _selectedRowFile);

    private Task OpenAsync()
    {
        var path = _selectedFile ?? _selectedRowFile;
        if (path is null || !File.Exists(path)) throw UiErrors.IO(new(TextKey.ConfigChanged));
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private async Task DeleteAsync(ConfigSnapshot snapshot, ConfigSource source)
    {
        if (!Editable) return;
        _editing = true;
        changed();
        try
        {
            if (!await MessageBox.ConfirmAsync(L(TextKey.ConfigDeleteConfirm, source.Name, snapshot.Path), PromptIconKind.Warning, owner: owner)) return;
            _editing = false;
            await SaveAsync(snapshot, source, null, null, false);
        }
        finally { _editing = false; changed(); Render(); }
    }

    private async Task EditAsync(ConfigSnapshot? snapshot, ConfigSource? source)
    {
        if (!Editable || _directory is null) return;
        _editing = true;
        changed();
        try
        {
            var writable = _snapshots.Where(s => !s.IsReadOnly).ToArray();
            var target = snapshot?.Path ?? Path.Combine(_directory, "NuGet.Config");
            var paths = writable.Select(s => s.Path).Append(target).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var result = await new SourceEditor(owner, format).ShowAsync(paths, target, source);
            if (result is null) return;
            snapshot ??= _snapshots.FirstOrDefault(s => s.Path == result.Path) ?? NuGetConfiguration.Read(result.Path);
            _editing = false;
            await SaveAsync(snapshot, source, result.Source, result.Password, result.RemoveCredentials, securityConfirmed: true);
        }
        finally { _editing = false; changed(); Render(); }
    }

    private async Task SaveAsync(ConfigSnapshot snapshot, ConfigSource? original, ConfigSource? replacement,
        string? password, bool removeCredentials, bool securityConfirmed = false)
    {
        if (!Editable) return;
        IsSaving = true;
        changed();
        try
        {
            if (!securityConfirmed && replacement is { } source && (source.AllowInsecure || source.DisableTlsValidation)
                && !await MessageBox.ConfirmAsync(L(TextKey.ConfigSecurityWarning), PromptIconKind.Warning, owner: owner)) return;
            await NuGetConfiguration.SaveAsync(snapshot, original, replacement, password, removeCredentials, CancellationToken.None);
            Update(_directory, reload: true);
            await saved();
        }
        finally { IsSaving = false; changed(); Render(); }
    }

    private async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            await MessageBox.NotifyAsync(L(TextKey.ConfigSaveFailed, format(UiMessage.FromException(ex))), PromptIconKind.Error, owner: owner);
            Render();
        }
    }

    private static TextBlock Text(string text) => new TextBlock().Text(text).TextWrapping(TextWrapping.Wrap).CenterVertical();
    private static TextBlock Cell(string text) =>
        new TextBlock().Text(text).TextTrimming(TextTrimming.CharacterEllipsis).ToolTip(text).CenterVertical().Margin(6, 8);
    private Button IconButton(GlyphKind glyph, TextKey tooltip, bool enabled, Func<Task> action) =>
        new Button().Content(new GlyphElement { Kind = glyph }.Width(16).Height(16))
            .Width(30).Height(30).Center().ToolTip(L(tooltip)).IsEnabled(enabled).OnClick(() => Run(action));
}

internal sealed record SourceEditResult(string Path, ConfigSource Source, string? Password, bool RemoveCredentials);

internal sealed class SourceEditor(Window owner, Func<UiMessage, string> format)
{
    private string L(TextKey key, params string[] arguments) => format(new(key, arguments));

    public async Task<SourceEditResult?> ShowAsync(IReadOnlyList<string> paths, string target, ConfigSource? source)
    {
        SourceEditResult? result = null;
        var dialog = new Window { Title = L(source is null ? TextKey.ConfigAdd : TextKey.ConfigEdit), StartupLocation = WindowStartupLocation.CenterOwner };
        dialog.Resizable(640, 620, minWidth: 500, minHeight: 480);
        var file = new ComboBox().Items(paths.ToArray()).SelectedIndex(paths.ToList().IndexOf(target)).IsEnabled(source is null);
        var name = new TextBox().Text(source?.Name ?? "");
        var address = new TextBox().Text(source?.Location ?? "https://");
        var username = new TextBox().Text(source?.Username ?? "");
        var password = new PasswordBox();
        var enabled = new CheckBox().Content(L(TextKey.ConfigEnabled)).IsChecked(source?.Enabled ?? true);
        var insecure = new CheckBox().Content(L(TextKey.ConfigAllowInsecure)).IsChecked(source?.AllowInsecure ?? false);
        var tls = new CheckBox().Content(L(TextKey.ConfigDisableTls)).IsChecked(source?.DisableTlsValidation ?? false);
        var error = new TextBlock().Foreground(AppTheme.Current.Error).TextWrapping(TextWrapping.Wrap);
        var fields = new Grid().Columns("150,*").Rows("Auto,Auto,Auto,Auto,Auto").Spacing(8);
        var labels = new[] { TextKey.ConfigFile, TextKey.ConfigName, TextKey.ConfigAddress, TextKey.ConfigUsername, TextKey.ConfigPassword };
        Element[] inputs = [file, name, address, username, password];
        for (var i = 0; i < inputs.Length; i++)
        {
            fields.Add(new TextBlock().Text(L(labels[i])).Row(i).CenterVertical());
            fields.Add(inputs[i].Row(i).Column(1));
        }
        var save = new Button().Content(L(TextKey.ConfigSave));
        var submitting = false;
        save.OnClick(async () =>
        {
            if (submitting) return;
            submitting = true;
            save.IsEnabled = false;
            try
            {
                if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(address.Text))
                    throw UiErrors.Argument(new(TextKey.ConfigNameAddressRequired));
                if (file.SelectedIndex < 0) throw UiErrors.Argument(new(TextKey.ConfigNameAddressRequired));
                if (password.Password.Length > 0 && string.IsNullOrWhiteSpace(username.Text))
                    throw UiErrors.Argument(new(TextKey.ConfigUsernameRequired));
                var remove = source?.HasCredentials == true && string.IsNullOrWhiteSpace(username.Text);
                if (remove && !await MessageBox.ConfirmAsync(L(TextKey.ConfigRemoveCredentials), PromptIconKind.Warning, owner: dialog)) return;
                if ((insecure.IsChecked == true || tls.IsChecked == true)
                    && !await MessageBox.ConfirmAsync(L(TextKey.ConfigSecurityWarning), PromptIconKind.Warning, owner: dialog)) return;
                var replacement = new ConfigSource(name.Text.Trim(), address.Text.Trim(), enabled.IsChecked == true,
                    insecure.IsChecked == true, tls.IsChecked == true, username.Text.Trim(), source?.HasCredentials ?? false);
                NuGetConfiguration.Validate(replacement);
                result = new(paths[file.SelectedIndex], replacement, password.Password.Length > 0 ? password.Password : null, remove);
                password.Password = "";
                dialog.Close();
            }
            catch (ArgumentException ex) { error.Text = format(UiMessage.FromException(ex)); }
            finally { submitting = false; save.IsEnabled = true; }
        });
        dialog.Content = new DockPanel().Margin(16).Children(
            new StackPanel().Horizontal().Spacing(8).HorizontalAlignment(HorizontalAlignment.Right).DockBottom().Margin(0, 12, 0, 0)
                .Children(new Button().Content(L(TextKey.Cancel)).OnClick(dialog.Close), save),
            new ScrollViewer().VerticalScroll(ScrollMode.Auto).Content(new StackPanel().Vertical().Spacing(10).Children(
                fields, new TextBlock().Text(L(TextKey.ConfigPasswordHint)).TextWrapping(TextWrapping.Wrap),
                enabled, insecure, tls, new TextBlock().Text(L(TextKey.ConfigCredentialHint)).TextWrapping(TextWrapping.Wrap), error)));
        try { await dialog.ShowDialogAsync(owner); return result; }
        finally { password.Password = ""; }
    }
}
