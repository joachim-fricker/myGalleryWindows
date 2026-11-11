using System.Windows;
using System.Windows.Controls;
using MyGallery.Domain;
using UiMessageBox = Wpf.Ui.Controls.MessageBox;
using UiResult = Wpf.Ui.Controls.MessageBoxResult;

namespace MyGallery.Views;

/// <summary>Kleine Dialoge (Namen eingeben, Person oder Gruppe wählen).</summary>
public static class Dialogs
{
    private static UiMessageBox Box(string title, UIElement content, string ok) => new()
    {
        Title = title,
        Content = content,
        PrimaryButtonText = ok,
        CloseButtonText = "Abbrechen",
        MinWidth = 420,
    };

    /// <summary>Text eingeben; null = abgebrochen.</summary>
    public static async Task<string?> AskTextAsync(string title, string label, string initial, string ok = "Speichern")
    {
        var box = new TextBox { Text = initial, MinWidth = 360 };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        var dialog = Box(title, panel, ok);
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) { dialog.Tag = "ok"; dialog.Close(); }
        };
        var result = await dialog.ShowDialogAsync();
        return result == UiResult.Primary || Equals(dialog.Tag, "ok") ? box.Text.Trim() : null;
    }

    /// <summary>Ja/Nein-Frage.</summary>
    public static async Task<bool> ConfirmAsync(string title, string text, string ok)
    {
        var dialog = Box(title, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 }, ok);
        return await dialog.ShowDialogAsync() == UiResult.Primary;
    }

    /// <summary>Hinweis mit nur einem Knopf.</summary>
    public static async Task InfoAsync(string title, string text)
    {
        var dialog = new UiMessageBox
        {
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 },
            CloseButtonText = "OK",
            MinWidth = 420,
        };
        await dialog.ShowDialogAsync();
    }

    /// <summary>Gruppe wählen; null = abgebrochen.</summary>
    public static async Task<int?> PickGroupAsync(IReadOnlyList<Data.GroupDef> groups, int? current)
    {
        var list = new ListBox { MaxHeight = 360, MinWidth = 360 };
        foreach (var g in groups) list.Items.Add(new ListBoxItem { Content = g.Name, Tag = g.Id, IsSelected = g.Id == current });
        var dialog = Box("Gruppe wählen", list, "Übernehmen");
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is not null) { dialog.Tag = "ok"; dialog.Close(); } };
        var result = await dialog.ShowDialogAsync();
        if (result != UiResult.Primary && !Equals(dialog.Tag, "ok")) return null;
        return (list.SelectedItem as ListBoxItem)?.Tag as int?;
    }

    /// <summary>Ergebnis der Personenauswahl: bestehende Person oder neuer Name.</summary>
    public sealed record PersonChoice(int? PersonId, string? NewName);

    /// <summary>Person wählen (mit Suche) oder neue Person anlegen; null = abgebrochen.</summary>
    public static async Task<PersonChoice?> PickPersonAsync(string title, IReadOnlyList<PersonInfo> persons, bool allowNew)
    {
        var search = new TextBox { MinWidth = 360 };
        var list = new ListBox { Height = 320, MinWidth = 360, Margin = new Thickness(0, 8, 0, 0) };
        void Fill()
        {
            var q = search.Text.Trim();
            list.Items.Clear();
            foreach (var p in persons.Where(p => q.Length == 0 || p.DisplayName.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
            {
                var text = p.Name is null ? $"Unbenannt ({p.Images.Count} Bilder)" : $"{p.Name}  ({p.Images.Count})";
                list.Items.Add(new ListBoxItem { Content = text, Tag = p.Id });
            }
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        search.TextChanged += (_, _) => Fill();
        Fill();
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = allowNew ? "Suchen – oder einen neuen Namen eingeben und „Neue Person“ wählen:" : "Suchen:",
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(search);
        panel.Children.Add(list);
        var dialog = Box(title, panel, "Übernehmen");
        if (allowNew) dialog.SecondaryButtonText = "Neue Person";
        dialog.Loaded += (_, _) => search.Focus();
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is not null) { dialog.Tag = "ok"; dialog.Close(); } };
        var result = await dialog.ShowDialogAsync();
        if (result == UiResult.Secondary && allowNew)
        {
            var name = search.Text.Trim();
            if (name.Length == 0) name = await AskTextAsync("Neue Person", "Name der neuen Person:", "", "Anlegen") ?? "";
            return name.Length == 0 ? null : new PersonChoice(null, name);
        }
        if (result != UiResult.Primary && !Equals(dialog.Tag, "ok")) return null;
        return (list.SelectedItem as ListBoxItem)?.Tag is int id ? new PersonChoice(id, null) : null;
    }

    /// <summary>
    /// Gruppen bearbeiten: Reihenfolge, umbenennen, löschen, neu. Ergebnis wie für FaceStore.ApplyGroups
    /// (id &lt;= 0 = neue Gruppe); null = abgebrochen.
    /// </summary>
    public static async Task<List<Data.GroupDef>?> EditGroupsAsync(IReadOnlyList<Data.GroupDef> groups)
    {
        var items = groups.ToList();
        int nextNew = 0;
        var list = new ListBox { Height = 300, MinWidth = 300 };
        void Fill(int select)
        {
            list.Items.Clear();
            foreach (var g in items)
                list.Items.Add(g.Id == Data.FaceStore.MainGroup ? $"{g.Name}  (immer sichtbar)" : g.Name);
            if (items.Count > 0) list.SelectedIndex = Math.Clamp(select, 0, items.Count - 1);
        }
        Fill(0);

        Button Btn(string text, Func<Task> action)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += async (_, _) => await action();
            return b;
        }
        var buttons = new StackPanel { Width = 150, Margin = new Thickness(12, 0, 0, 0) };
        buttons.Children.Add(Btn("Nach oben", () =>
        {
            int i = list.SelectedIndex;
            if (i > 0) { (items[i - 1], items[i]) = (items[i], items[i - 1]); Fill(i - 1); }
            return Task.CompletedTask;
        }));
        buttons.Children.Add(Btn("Nach unten", () =>
        {
            int i = list.SelectedIndex;
            if (i >= 0 && i < items.Count - 1) { (items[i + 1], items[i]) = (items[i], items[i + 1]); Fill(i + 1); }
            return Task.CompletedTask;
        }));
        buttons.Children.Add(Btn("Umbenennen …", async () =>
        {
            int i = list.SelectedIndex;
            if (i < 0) return;
            var name = await AskTextAsync("Gruppe umbenennen", "Name der Gruppe:", items[i].Name);
            if (!string.IsNullOrWhiteSpace(name)) { items[i] = items[i] with { Name = name }; Fill(i); }
        }));
        buttons.Children.Add(Btn("Löschen", () =>
        {
            int i = list.SelectedIndex;
            if (i >= 0 && items[i].Id != Data.FaceStore.MainGroup) { items.RemoveAt(i); Fill(i); }
            return Task.CompletedTask;
        }));
        buttons.Children.Add(Btn("Neue Gruppe …", async () =>
        {
            var name = await AskTextAsync("Neue Gruppe", "Name der neuen Gruppe:", "", "Anlegen");
            if (!string.IsNullOrWhiteSpace(name)) { items.Add(new Data.GroupDef(--nextNew, name)); Fill(items.Count - 1); }
        }));

        var panel = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        panel.Children.Add(buttons);
        panel.Children.Add(list);
        var outer = new StackPanel();
        outer.Children.Add(new TextBlock
        {
            Text = "Personen einer gelöschten Gruppe kommen nach „Haupt“.",
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        });
        outer.Children.Add(panel);
        var dialog = Box("Gruppen bearbeiten", outer, "Übernehmen");
        dialog.MinWidth = 520;
        return await dialog.ShowDialogAsync() == UiResult.Primary ? items : null;
    }

    /// <summary>Reihenfolge der Personen einer Gruppe ändern; Ergebnis (Gruppe, Personen-IDs) oder null.</summary>
    public static async Task<(int GroupId, List<int> Order)?> EditOrderAsync(
        IReadOnlyList<Data.GroupDef> groups, IReadOnlyList<PersonInfo> persons)
    {
        var groupBox = new ComboBox { MinWidth = 300, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var g in groups) groupBox.Items.Add(new ComboBoxItem { Content = g.Name, Tag = g.Id });
        var list = new ListBox { Height = 320, MinWidth = 300 };
        var items = new List<PersonInfo>();
        void Load()
        {
            int gid = (groupBox.SelectedItem as ComboBoxItem)?.Tag as int? ?? Data.FaceStore.MainGroup;
            items = persons.Where(p => p.Name is not null && (p.GroupId ?? Data.FaceStore.MainGroup) == gid).ToList();
            Fill(0);
        }
        void Fill(int select)
        {
            list.Items.Clear();
            foreach (var p in items) list.Items.Add(p.DisplayName);
            if (items.Count > 0) list.SelectedIndex = Math.Clamp(select, 0, items.Count - 1);
        }
        groupBox.SelectionChanged += (_, _) => Load();
        groupBox.SelectedIndex = 0;

        Button Btn(string text, int delta)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
            b.Click += (_, _) =>
            {
                int i = list.SelectedIndex, j = i + delta;
                if (i < 0 || j < 0 || j >= items.Count) return;
                (items[i], items[j]) = (items[j], items[i]);
                Fill(j);
            };
            return b;
        }
        var buttons = new StackPanel { Width = 150, Margin = new Thickness(12, 0, 0, 0) };
        buttons.Children.Add(Btn("Nach oben", -1));
        buttons.Children.Add(Btn("Nach unten", +1));
        var top = new Button { Content = "Ganz nach oben", Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
        top.Click += (_, _) =>
        {
            int i = list.SelectedIndex;
            if (i <= 0) return;
            var p = items[i];
            items.RemoveAt(i);
            items.Insert(0, p);
            Fill(0);
        };
        buttons.Children.Add(top);

        var panel = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        panel.Children.Add(buttons);
        panel.Children.Add(list);
        var outer = new StackPanel();
        outer.Children.Add(groupBox);
        outer.Children.Add(panel);
        var dialog = Box("Reihenfolge der Personen", outer, "Übernehmen");
        dialog.MinWidth = 520;
        if (await dialog.ShowDialogAsync() != UiResult.Primary) return null;
        int groupId = (groupBox.SelectedItem as ComboBoxItem)?.Tag as int? ?? Data.FaceStore.MainGroup;
        return (groupId, items.Select(p => p.Id).ToList());
    }
}
