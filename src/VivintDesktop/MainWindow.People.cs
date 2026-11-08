using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VivintDesktop.Api;
using VivintDesktop.Views;

namespace VivintDesktop;

/// <summary>People tab: panel users, panel/lock PINs, per-lock access, invites.</summary>
public partial class MainWindow
{
    private bool _peopleLoaded;

    private void LoadPeopleTab()
    {
        PeopleContent.Children.Clear();
        if (_state is null || _systemJson is not { } sj) { PeopleContent.Children.Add(Muted("Loading…")); return; }
        _peopleLoaded = true;
        var pid = _state.PanelId;
        var users = PanelUser.Parse(sj);
        var locks = _state.Devices.Where(d => d.Category == DeviceCategory.Lock).OrderBy(d => d.Name).ToList();

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var add = MakeBtn("+ Add person", () => AddPersonAsync(locks), "PrimaryButton");
        DockPanel.SetDock(add, Dock.Right);
        head.Children.Add(add);
        head.Children.Add(new TextBlock { Text = $"{users.Count} {(users.Count == 1 ? "person" : "people")} on this system", Foreground = Res("Muted"),
            VerticalAlignment = VerticalAlignment.Center });
        PeopleContent.Children.Add(head);
        PeopleContent.Children.Add(SectionNote("Panel PINs disarm the system at the keypad. Lock PINs open the locks you allow — click a lock to allow or remove it."));

        var rows = new List<UIElement>();
        foreach (var u in users.OrderByDescending(u => u.PrimaryAdmin).ThenByDescending(u => u.Admin).ThenBy(u => u.Name))
        {
            var sub = new List<string> { u.PrimaryAdmin ? "Owner" : u.Admin ? "Admin" : "User" };
            sub.Add(u.HasPanelPin ? "panel PIN set" : "no panel PIN");
            sub.Add(u.HasLockPin ? "lock PIN set" : "no lock PIN");
            if (!u.Registered) sub.Add("not using the app");

            var trail = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var l in locks)
            {
                var lockId = l.Id;
                var chip = Chip("  " + l.Name, u.LockIds.Contains(l.Id), async allow =>
                {
                    await Do(() => _svc.SetLockAccessAsync(pid, u.Id, lockId, allow),
                        $"{(allow ? "Giving" : "Removing")} {u.Name} access to {l.Name}");
                    await AfterPeopleChange();
                });
                chip.FontFamily = new FontFamily("Segoe UI, Segoe Fluent Icons");
                chip.ToolTip = u.LockIds.Contains(l.Id) ? $"Can open {l.Name} with a lock PIN" : $"Can't open {l.Name}";
                chip.Margin = new Thickness(0, 0, 6, 0);
                trail.Children.Add(chip);
            }
            var actions = new List<(string, Func<Task>, bool)>
            {
                ("Change panel PIN…", () => ChangePinAsync(u, lockPin: false), false),
                ("Change lock PIN…", () => ChangePinAsync(u, lockPin: true), false),
                ("Rename…", () => RenamePersonAsync(u), false),
            };
            if (!u.Registered) actions.Add(("Invite to the app…", () => InvitePersonAsync(u), false));
            if (!u.PrimaryAdmin) actions.Add(("Remove from system", () => RemovePersonAsync(u), true));
            trail.Children.Add(MoreMenu(actions.ToArray()));
            rows.Add(ListRow(Avatar(u.Name), u.Name, string.Join(" · ", sub), trail));
        }
        PeopleContent.Children.Add(ListCard(rows));
    }

    private async Task AfterPeopleChange()
    {
        await RefreshAsync(quiet: true);
        LoadPeopleTab();
    }

    private async Task AddPersonAsync(List<Device> locks)
    {
        if (_state is null) return;
        var pid = _state.PanelId;
        var f = new Prompt("Add person", "Adds a user to the panel. To give them the app too, use Invite afterwards.")
            .Text("name", "Name", maxLength: 40)
            .Text("pin", "Panel PIN (4 digits, optional)", secret: true, maxLength: 4)
            .Text("lock", "Lock PIN (4 digits, optional)", secret: true, maxLength: 4)
            .Check("admin", "Admin (can change settings and other users)");
        if (!f.Ask(this, "Add", v =>
                v["name"].Length == 0 ? "Enter a name."
                : v["pin"].Length > 0 && !Prompt.IsPin(v["pin"]) ? "The panel PIN must be 4 digits."
                : v["lock"].Length > 0 && !Prompt.IsPin(v["lock"]) ? "The lock PIN must be 4 digits."
                : null)) return;
        var v = f.Values;
        int? newId = null;
        await Do(async () =>
        {
            var (r, id) = await _svc.CreatePanelUserAsync(pid, v["name"], v["pin"], v["lock"], v["admin"] == "1");
            newId = id;
            return r;
        }, $"Adding {v["name"]}");
        if (newId is { } nid && v["lock"].Length > 0)
            foreach (var l in locks)
                await _svc.SetLockAccessAsync(pid, nid.ToString(), l.Id, true);
        await AfterPeopleChange();
    }

    private async Task ChangePinAsync(PanelUser u, bool lockPin)
    {
        if (_state is null) return;
        var pid = _state.PanelId;
        var what = lockPin ? "lock PIN" : "panel PIN";
        var f = new Prompt($"Change {what}", $"New {what} for {u.Name}.")
            .Text("pin", "New PIN (4 digits)", secret: true, maxLength: 4)
            .Text("again", "Repeat PIN", secret: true, maxLength: 4);
        if (!f.Ask(this, "Save", v => !Prompt.IsPin(v["pin"]) ? "The PIN must be 4 digits."
                : v["pin"] != v["again"] ? "The PINs don't match." : null)) return;
        var pin = f.Values["pin"];
        await Do(() => lockPin ? _svc.SetLockPinAsync(pid, u.Id, pin) : _svc.SetPanelPinAsync(pid, u.Id, pin),
            $"Changing {u.Name}'s {what}");
        await AfterPeopleChange();
    }

    private async Task RenamePersonAsync(PanelUser u)
    {
        if (_state is null) return;
        var pid = _state.PanelId;
        var f = new Prompt("Rename").Text("name", "Name", u.Name, maxLength: 40);
        if (!f.Ask(this, "Save", v => v["name"].Length == 0 ? "Enter a name." : null)) return;
        await Do(() => _svc.RenameUserAsync(pid, u.Id, f.Values["name"]), $"Renaming {u.Name}");
        await AfterPeopleChange();
    }

    private async Task InvitePersonAsync(PanelUser u)
    {
        if (_state is null) return;
        var pid = _state.PanelId;
        var f = new Prompt("Invite to the Vivint app", $"Vivint emails {u.Name} an invite to create an account for this system.")
            .Text("email", "Their email address")
            .Text("msg", "Message (optional)");
        if (!f.Ask(this, "Send invite", v => !v["email"].Contains('@') ? "Enter an email address." : null)) return;
        await Do(() => _svc.SendInviteAsync(pid, u.Id, u.Name, f.Values["email"], f.Values["msg"]), $"Inviting {u.Name}");
    }

    private async Task RemovePersonAsync(PanelUser u)
    {
        if (_state is null) return;
        var pid = _state.PanelId;
        if (!Dialog.Confirm(this, $"Remove {u.Name}?", "Their panel and lock PINs stop working right away.", "Remove", danger: true)) return;
        await Do(() => _svc.DeletePanelUserAsync(pid, u.Id), $"Removing {u.Name}");
        await AfterPeopleChange();
    }
}
