// Arcfin Sync configuration page. Everything is drawn from the settings schema the plugin
// serves, and every value a user or device supplied is set with textContent, never as HTML.

var STATE = {
    schema: null,
    policy: null,
    saved: null,
    users: [],
    status: null,
    dirty: false
};

function api(path) {
    return ApiClient.getUrl("ArcfinSync/" + path);
}

function getJson(path) {
    return ApiClient.getJSON(api(path));
}

function send(method, path, body) {
    var options = { type: method, url: api(path) };
    if (body !== undefined) {
        options.data = JSON.stringify(body);
        options.contentType = "application/json";
        options.dataType = "json";
    }
    return ApiClient.ajax(options);
}

function el(tag, props, children) {
    var node = document.createElement(tag);
    if (props) {
        Object.keys(props).forEach(function (name) {
            var value = props[name];
            if (value === null || value === undefined || value === false) { return; }
            if (name === "text") { node.textContent = value; }
            else if (name === "className") { node.className = value; }
            else if (name.indexOf("on") === 0) { node.addEventListener(name.substring(2), value); }
            else if (value === true) { node.setAttribute(name, ""); }
            else { node.setAttribute(name, value); }
        });
    }
    (children || []).forEach(function (child) {
        if (child === null || child === undefined) { return; }
        node.appendChild(typeof child === "string" ? document.createTextNode(child) : child);
    });
    return node;
}

function errorText(err) {
    if (err && typeof err.text === "function") {
        return err.text().then(function (t) { return t || ("HTTP " + err.status); });
    }
    return Promise.resolve((err && err.message) || "Something went wrong.");
}

function showError(err) {
    Dashboard.hideLoadingMsg();
    errorText(err).then(function (text) { Dashboard.alert({ title: "Arcfin Sync", message: text }); });
}

function clone(value) {
    return JSON.parse(JSON.stringify(value));
}

// ----- Schema helpers -----

function schemaSetting(key) {
    var list = STATE.schema.settings || [];
    for (var i = 0; i < list.length; i++) {
        if (list[i].key === key) { return list[i]; }
    }
    return null;
}

function settingTitle(key) {
    var s = schemaSetting(key);
    return (s && s.title) || key;
}

function describeValue(setting, value) {
    if (value === null || value === undefined) { return "Not set"; }
    if (setting && setting.type === "bool") { return value === "true" ? "On" : "Off"; }
    if (isJsonSetting(setting)) { return value.length > 80 ? value.substring(0, 77) + "…" : value; }
    if (setting && setting.options) {
        for (var i = 0; i < setting.options.length; i++) {
            if (setting.options[i].value === value) { return setting.options[i].title || value; }
        }
    }
    return value === "" ? "(empty)" : value;
}

function appDefaultLabel(setting) {
    if (!setting || setting.default === undefined || setting.default === null) { return "App default"; }
    return "App default: " + describeValue(setting, setting.default);
}

// A control for a setting's default value. "" in the control means no default.
function valueControl(setting, value, onChange, unsetLabel) {
    unsetLabel = unsetLabel || appDefaultLabel(setting);
    if (isJsonSetting(setting)) { return jsonControl(setting, value, onChange, unsetLabel); }
    var control;
    var type = setting ? setting.type : "string";
    if (type === "bool" || (setting && setting.options && setting.options.length)) {
        control = el("select", { className: "afControl", "aria-label": "Default for " + ((setting && setting.title) || "") });
        control.appendChild(el("option", { value: "", text: unsetLabel }));
        var options = type === "bool"
            ? [{ value: "true", title: "On" }, { value: "false", title: "Off" }]
            : setting.options;
        var known = false;
        options.forEach(function (o) {
            if (o.value === value) { known = true; }
            control.appendChild(el("option", { value: o.value, text: o.title || (o.value === "" ? "None" : o.value) }));
        });
        if (value !== null && value !== undefined && !known) {
            control.appendChild(el("option", { value: value, text: value + " (current)" }));
        }
        if (type !== "bool" && options.some(function (o) { return o.value === ""; })) {
            // An option whose value is "" can't share the "no default" slot.
            control.firstChild.value = "__arcfinUnset";
            control.value = value === null || value === undefined ? "__arcfinUnset" : value;
        } else {
            control.value = value === null || value === undefined ? "" : value;
        }
    } else if (type === "int" || type === "double") {
        control = el("input", {
            type: "number", className: "afControl", inputmode: type === "int" ? "numeric" : "decimal",
            min: setting.min, max: setting.max, step: setting.step || (type === "int" ? 1 : "any"),
            placeholder: unsetLabel, "aria-label": "Default for " + (setting.title || "")
        });
        control.value = value === null || value === undefined ? "" : value;
    } else {
        control = el("input", { type: "text", className: "afControl", placeholder: unsetLabel, maxlength: "4096", "aria-label": "Default for " + ((setting && setting.title) || "") });
        control.value = value === null || value === undefined ? "" : value;
    }
    control.addEventListener("change", function () {
        var v = control.value;
        if (v === "__arcfinUnset") { v = null; }
        else if (v === "" && (type === "bool" || type === "int" || type === "double" || control.tagName === "SELECT")) { v = null; }
        else if (v === "" && type === "string" && control.tagName === "INPUT") { v = null; }
        onChange(v);
    });
    return control;
}

function isJsonSetting(setting) {
    return !!setting && (setting.type === "json" || setting.format === "json");
}

// A setting the app keeps as a JSON array or object: a short preview, and raw JSON to edit.
function jsonControl(setting, value, onChange, unsetLabel) {
    var current = value === null || value === undefined ? null : value;
    var preview = el("summary", { text: current === null ? unsetLabel : describeValue(setting, current) });
    var area = el("textarea", { className: "afControl afMono", rows: "6", spellcheck: "false", "aria-label": "JSON default for " + (setting.title || setting.key) });
    area.value = current === null ? "" : current;
    var message = el("div", { className: "afRowMeta", text: "A JSON array or object." });
    var apply = function () {
        var text = area.value.trim();
        if (text === "") { message.textContent = "No default."; onChange(null); preview.textContent = unsetLabel; return; }
        var parsed;
        try { parsed = JSON.parse(text); } catch (e) { parsed = undefined; }
        if (parsed === null || typeof parsed !== "object") {
            message.textContent = "That isn't a JSON array or object, so it wasn't kept.";
            return;
        }
        var compact = JSON.stringify(parsed);
        if (compact.length > 4096) { message.textContent = "That's longer than 4096 characters, so it wasn't kept."; return; }
        area.value = compact;
        message.textContent = "A JSON array or object.";
        preview.textContent = describeValue(setting, compact);
        onChange(compact);
    };
    area.addEventListener("change", apply);
    var reset = el("button", {
        type: "button", className: "afSmallButton", text: "Reset", onclick: function () { area.value = ""; apply(); }
    });
    return el("details", { className: "afJson" }, [preview, area, el("div", { className: "afButtons" }, [reset]), message]);
}

function lockToggle(locked, onChange, label) {
    var box = el("input", { type: "checkbox", "aria-label": label });
    box.checked = !!locked;
    box.addEventListener("change", function () { onChange(box.checked); });
    return el("label", { className: "afLock", title: "Everyone gets this value and can't change it in Arcfin" }, [box, el("span", { text: "Lock" })]);
}

// A user's lock: the server-wide one (null), or their own either way.
function userLockChoice(locked, onChange, label) {
    var select = el("select", { className: "afControl", "aria-label": label });
    [["", "Same as everyone"], ["true", "Locked"], ["false", "Not locked"]].forEach(function (o) {
        select.appendChild(el("option", { value: o[0], text: o[1] }));
    });
    select.value = locked === true ? "true" : (locked === false ? "false" : "");
    select.addEventListener("change", function () { onChange(select.value === "" ? null : select.value === "true"); });
    return select;
}

// ----- Policy model -----

// Locked, or a default that isn't the app's own. The 0.3 page saved every row with the value it
// showed, so a default equal to the app's counts as unchanged.
function isChanged(entry, setting) {
    if (!entry) { return false; }
    if (entry.Locked) { return true; }
    if (entry.DefaultValue === null || entry.DefaultValue === undefined) { return false; }
    return !setting || setting.default === undefined || setting.default === null || entry.DefaultValue !== setting.default;
}

function findEntry(list, key) {
    for (var i = 0; i < list.length; i++) {
        if (list[i].Key === key) { return list[i]; }
    }
    return null;
}

function setEntry(list, key, change) {
    var entry = findEntry(list, key);
    if (!entry) {
        entry = { Key: key, DefaultValue: null, Locked: false };
        list.push(entry);
    }
    change(entry);
    return entry;
}

function markDirty() {
    STATE.dirty = JSON.stringify(STATE.policy) !== JSON.stringify(STATE.saved);
    var bar = document.querySelector("#afSaveBar");
    if (bar) { bar.hidden = !STATE.dirty; }
}

function cleanPolicy(policy) {
    var result = { Settings: [], Users: [] };
    policy.Settings.forEach(function (s) {
        if (s.Locked || (s.DefaultValue !== null && s.DefaultValue !== undefined)) { result.Settings.push(s); }
    });
    policy.Users.forEach(function (u) {
        var own = u.Settings.filter(function (s) {
            return (s.Locked !== null && s.Locked !== undefined) || (s.DefaultValue !== null && s.DefaultValue !== undefined);
        });
        if (own.length) { result.Users.push({ UserId: u.UserId, Settings: own }); }
    });
    return result;
}

function savePolicy(view) {
    Dashboard.showLoadingMsg();
    return send("PUT", "Admin/Policy", cleanPolicy(STATE.policy)).then(function (saved) {
        STATE.policy = saved;
        STATE.saved = clone(saved);
        markDirty();
        renderAll(view);
        Dashboard.hideLoadingMsg();
        Dashboard.processPluginConfigurationUpdateResult();
    }, showError);
}

// ----- Overview -----

function renderOverview(view) {
    var stats = view.querySelector("#afStats");
    stats.innerHTML = "";
    var devices = 0;
    STATE.users.forEach(function (u) { devices += u.Devices.length; });
    var locked = STATE.policy.Settings.filter(function (s) { return s.Locked; }).length;
    var defaults = STATE.policy.Settings.filter(function (s) { return !s.Locked && isChanged(s, schemaSetting(s.Key)); }).length;
    var overrides = STATE.policy.Users.filter(function (u) { return u.Settings.length; }).length;
    [
        [STATE.status.Installed ? "Active" : "Not loaded", "Plugin status"],
        [String(STATE.users.length), STATE.users.length === 1 ? "User" : "Users"],
        [String(devices), devices === 1 ? "Arcfin device" : "Arcfin devices"],
        [String(locked), "Locked settings"],
        [String(defaults), "Defaults set"],
        [String(overrides), overrides === 1 ? "User with overrides" : "Users with overrides"]
    ].forEach(function (pair) {
        stats.appendChild(el("div", { className: "afStat" }, [
            el("div", { className: "afStatValue", text: pair[0] }),
            el("div", { className: "afStatLabel", text: pair[1] })
        ]));
    });
    view.querySelector("#afVersion").textContent = "Version " + STATE.status.Version + " · API " + STATE.status.ApiVersion;
}

// ----- Settings tab -----

function settingRow(setting, entries, options) {
    var key = setting.key;
    var entry = findEntry(entries, key);
    var changed = isChanged(entry, setting) || !!options.isOverride;
    var row = el("div", { className: "afRow" + (changed ? " afChanged" : ""), "data-key": key });
    row.dataset.search = ((setting.title || "") + " " + key + " " + (setting.detail || "")).toLowerCase();
    row.dataset.changed = changed ? "true" : "false";

    var meta = [el("span", { className: "afKey", text: key })];
    if (setting.scope === "device") { meta.push(" "); meta.push(el("span", { className: "afBadge", text: "This device" })); }
    if (setting.platforms && setting.platforms.length && setting.platforms.length < 3) {
        meta.push(" "); meta.push(el("span", { className: "afBadge", text: setting.platforms.join(", ") }));
    }
    var label = el("div", null, [
        el("div", { className: "afRowTitle", text: setting.title || key }),
        setting.detail ? el("div", { className: "afRowMeta", text: setting.detail }) : null,
        el("div", { className: "afRowMeta" }, meta)
    ]);
    row.appendChild(label);

    if (key === "seerrUsername") {
        row.appendChild(el("div", { className: "afNote", text: "Follows Seerr Account Type: lock that to Jellyfin Account and every user's Seerr username is their Jellyfin username. Otherwise each user enters their own." }));
        return row;
    }

    var update = function (change) {
        setEntry(entries, key, change);
        var e = findEntry(entries, key);
        var nowChanged = isChanged(e, setting) || !!options.isOverride;
        row.classList.toggle("afChanged", nowChanged);
        row.dataset.changed = nowChanged ? "true" : "false";
        markDirty();
        if (options.onChange) { options.onChange(); }
    };

    var controlCell = el("div", { className: "afRowControl" });
    var drawControl = function () {
        var current = findEntry(entries, key);
        controlCell.innerHTML = "";
        controlCell.appendChild(valueControl(setting, current ? current.DefaultValue : null, function (v) { update(function (e) { e.DefaultValue = v; }); }, options.unsetLabel));
    };
    drawControl();
    row.appendChild(controlCell);

    var actions = el("div", { className: "afDeviceActions" }, [
        options.isOverride ? userLockChoice(entry ? entry.Locked : null, function (choice) {
            update(function (e) { e.Locked = choice; });
        }, "Lock " + (setting.title || key) + " for this user") : lockToggle(entry && entry.Locked, function (on) {
            update(function (e) {
                e.Locked = on;
                // A server-wide lock pins a value: with none chosen, it's the app's default.
                if (on && !options.isOverride && (e.DefaultValue === null || e.DefaultValue === undefined) && setting.default !== undefined && setting.default !== null) {
                    e.DefaultValue = setting.default;
                    drawControl();
                }
            });
        }, "Lock " + (setting.title || key))
    ]);
    if (options.onRemove) {
        actions.appendChild(el("button", { type: "button", className: "afSmallButton", text: "Remove", "aria-label": "Remove override for " + (setting.title || key), onclick: options.onRemove }));
    }
    row.appendChild(actions);
    return row;
}

function renderSettings(view) {
    var container = view.querySelector("#afSettingsGroups");
    container.innerHTML = "";
    var groups = (STATE.schema.groups || []).slice();
    var byGroup = {};
    (STATE.schema.settings || []).forEach(function (s) {
        var g = s.group || "other";
        (byGroup[g] = byGroup[g] || []).push(s);
        if (!groups.some(function (x) { return x.id === g; })) { groups.push({ id: g, title: g }); }
    });

    groups.forEach(function (group) {
        var settings = byGroup[group.id];
        if (!settings || !settings.length) { return; }
        var rows = el("div", { className: "afRows" });
        settings.forEach(function (s) { rows.appendChild(settingRow(s, STATE.policy.Settings, {})); });
        container.appendChild(el("div", { className: "afGroup", "data-group": group.id }, [
            el("h3", { className: "afGroupTitle" }, [group.title || group.id, el("span", { className: "afGroupCount", text: String(settings.length) })]),
            rows
        ]));
    });

    filterSettings(view);
}

function filterSettings(view) {
    var query = (view.querySelector("#afSettingsSearch").value || "").trim().toLowerCase();
    var onlyChanged = view.querySelector("#afOnlyChanged").checked;
    var any = false;
    view.querySelectorAll("#afSettingsGroups .afGroup").forEach(function (group) {
        var visible = 0;
        group.querySelectorAll(".afRow").forEach(function (row) {
            var show = (!query || row.dataset.search.indexOf(query) !== -1) && (!onlyChanged || row.dataset.changed === "true");
            row.hidden = !show;
            if (show) { visible++; }
        });
        group.hidden = visible === 0;
        if (visible) { any = true; }
    });
    view.querySelector("#afSettingsEmpty").hidden = any;
}

// ----- Users & Devices tab -----

function relativeTime(iso) {
    var then = new Date(iso);
    if (isNaN(then.getTime())) { return ""; }
    var minutes = Math.round((Date.now() - then.getTime()) / 60000);
    if (minutes < 2) { return "just now"; }
    if (minutes < 60) { return minutes + " minutes ago"; }
    var hours = Math.round(minutes / 60);
    if (hours < 24) { return hours === 1 ? "an hour ago" : hours + " hours ago"; }
    var days = Math.round(hours / 24);
    if (days < 30) { return days === 1 ? "yesterday" : days + " days ago"; }
    return then.toLocaleDateString();
}

var SYNC_MODES = { automatic: "Syncs automatically", ask: "Asks first", off: "Sync off" };

function deviceRow(view, user, device) {
    var meta = [device.Platform, device.Model, device.AppVersion ? "Arcfin " + device.AppVersion : null, SYNC_MODES[device.SyncMode] || device.SyncMode, "Last seen " + relativeTime(device.LastSeen)]
        .filter(function (x) { return x; }).join(" · ");
    var wrap = el("div", { className: "afDevice" });
    var renameBox = el("div", { className: "afRename", hidden: true });
    var nameInput = el("input", { type: "text", className: "afControl", maxlength: "64", placeholder: device.JellyfinDeviceName, "aria-label": "Device name" });
    nameInput.value = device.CustomName || "";
    renameBox.appendChild(nameInput);
    renameBox.appendChild(el("button", {
        type: "button", className: "afSmallButton", text: "Save Name", onclick: function () {
            Dashboard.showLoadingMsg();
            send("PUT", "Admin/Users/" + encodeURIComponent(user.UserId) + "/Devices/" + encodeURIComponent(device.DeviceId) + "/Name", { Name: nameInput.value })
                .then(function () { return reload(view, "users"); }, showError);
        }
    }));
    renameBox.appendChild(el("span", { className: "afRowMeta", text: "Leave empty to use the name Jellyfin has for it." }));

    var head = el("div", { className: "afDeviceHead" }, [
        el("span", { className: "afDeviceName", text: device.Name || device.JellyfinDeviceName || device.DeviceId }),
        device.CustomName && device.JellyfinDeviceName ? el("span", { className: "afBadge", text: device.JellyfinDeviceName }) : null,
        el("span", { className: "afDeviceMeta", text: meta }),
        el("div", { className: "afDeviceActions" }, [
            el("button", { type: "button", className: "afSmallButton", text: "Rename", onclick: function () { renameBox.hidden = !renameBox.hidden; if (!renameBox.hidden) { nameInput.focus(); } } }),
            el("button", {
                type: "button", className: "afSmallButton afDanger", text: "Forget", onclick: function () {
                    if (!window.confirm("Forget " + (device.Name || "this device") + "? It stays signed in and appears again the next time it syncs.")) { return; }
                    Dashboard.showLoadingMsg();
                    send("DELETE", "Admin/Users/" + encodeURIComponent(user.UserId) + "/Devices/" + encodeURIComponent(device.DeviceId))
                        .then(function () { return reload(view, "users"); }, showError);
                }
            })
        ])
    ]);
    wrap.appendChild(head);
    wrap.appendChild(renameBox);

    var keys = Object.keys(device.Settings || {}).sort(function (a, b) { return settingTitle(a).localeCompare(settingTitle(b)); });
    if (keys.length) {
        var kv = el("div", { className: "afKv" });
        keys.forEach(function (k) {
            kv.appendChild(el("div", { text: settingTitle(k) }));
            kv.appendChild(el("div", { text: describeValue(schemaSetting(k), device.Settings[k]) }));
        });
        wrap.appendChild(el("details", { className: "afDeviceSettings" }, [el("summary", { text: "Settings on this device (" + keys.length + ")" }), kv]));
    }
    return wrap;
}

function overridesFor(userId, create) {
    for (var i = 0; i < STATE.policy.Users.length; i++) {
        if (STATE.policy.Users[i].UserId === userId) { return STATE.policy.Users[i]; }
    }
    if (!create) { return { UserId: userId, Settings: [] }; }
    var created = { UserId: userId, Settings: [] };
    STATE.policy.Users.push(created);
    return created;
}

function serverDefaultLabel(setting, key) {
    var global = findEntry(STATE.policy.Settings, key);
    if (global && global.DefaultValue !== null && global.DefaultValue !== undefined) {
        return "Server default: " + describeValue(setting, global.DefaultValue);
    }
    return appDefaultLabel(setting);
}

function overridesSection(view, user) {
    var holder = el("div");
    var draw = function () {
        holder.innerHTML = "";
        var record = overridesFor(user.UserId);
        var rows = el("div", { className: "afRows" });
        record.Settings.forEach(function (entry) {
            var setting = schemaSetting(entry.Key) || { key: entry.Key, type: "string", title: entry.Key };
            rows.appendChild(settingRow(setting, record.Settings, {
                isOverride: true,
                unsetLabel: serverDefaultLabel(setting, entry.Key),
                onRemove: function () {
                    record.Settings = record.Settings.filter(function (x) { return x.Key !== entry.Key; });
                    markDirty();
                    draw();
                }
            }));
        });
        if (record.Settings.length) {
            holder.appendChild(rows);
        } else {
            holder.appendChild(el("div", { className: "afRowMeta", text: "No overrides. This user gets the server-wide defaults and locks." }));
        }
        var picker = el("select", { className: "afControl", "aria-label": "Setting to override" });
        picker.appendChild(el("option", { value: "", text: "Choose a setting to override…" }));
        (STATE.schema.settings || []).forEach(function (s) {
            if (s.key === "seerrUsername" || findEntry(record.Settings, s.key)) { return; }
            picker.appendChild(el("option", { value: s.key, text: (s.title || s.key) + " (" + s.key + ")" }));
        });
        holder.appendChild(el("div", { className: "afOverrideAdd" }, [
            picker,
            el("button", {
                type: "button", className: "afSmallButton", text: "Add Override", onclick: function () {
                    if (!picker.value) { return; }
                    record = overridesFor(user.UserId, true);
                    // Nothing of its own yet: the server-wide default and lock until one is chosen.
                    record.Settings.push({ Key: picker.value, DefaultValue: null, Locked: null });
                    markDirty();
                    draw();
                }
            })
        ]));
    };
    draw();
    return holder;
}

function renderUsers(view) {
    var container = view.querySelector("#afUsers");
    var open = {};
    container.querySelectorAll("details.afUser[open]").forEach(function (d) { open[d.dataset.userId] = true; });
    container.innerHTML = "";
    if (!STATE.users.length) {
        container.appendChild(el("div", { className: "afEmpty", text: "No users." }));
        return;
    }
    STATE.users.forEach(function (user) {
        var overrideCount = overridesFor(user.UserId).Settings.length;
        var summary = el("summary", null, [
            el("span", { className: "afUserName", text: user.Name }),
            user.IsAdministrator ? el("span", { className: "afBadge afBadgeAccent", text: "Administrator" }) : null,
            el("span", { className: "afBadge", text: user.Devices.length + (user.Devices.length === 1 ? " device" : " devices") }),
            overrideCount ? el("span", { className: "afBadge", text: overrideCount + (overrideCount === 1 ? " override" : " overrides") }) : null
        ]);
        var body = el("div", { className: "afUserBody" });
        body.appendChild(el("h4", { className: "afSubTitle", text: "Devices" }));
        if (user.Devices.length) {
            user.Devices.forEach(function (d) { body.appendChild(deviceRow(view, user, d)); });
        } else {
            body.appendChild(el("div", { className: "afRowMeta", text: "No Arcfin devices yet. A device appears here after it signs in with an Arcfin version that supports devices." }));
        }
        body.appendChild(el("h4", { className: "afSubTitle", text: "Overrides for " + user.Name }));
        body.appendChild(overridesSection(view, user));
        var details = el("details", { className: "afUser", "data-user-id": user.UserId, open: !!open[user.UserId] }, [summary, body]);
        container.appendChild(details);
    });
}


// ----- Page -----

function renderAll(view) {
    renderOverview(view);
    renderSettings(view);
    renderUsers(view);
}

function selectTab(view, name) {
    view.querySelectorAll(".afTab").forEach(function (tab) {
        var on = tab.dataset.tab === name;
        tab.setAttribute("aria-selected", on ? "true" : "false");
        tab.tabIndex = on ? 0 : -1;
    });
    view.querySelectorAll(".afPanel").forEach(function (panel) { panel.hidden = panel.dataset.panel !== name; });
    try { sessionStorage.setItem("arcfinSyncTab", name); } catch (e) { /* private mode */ }
}

function reload(view, keepTab) {
    Dashboard.showLoadingMsg();
    return Promise.all([
        getJson("Schema"), getJson("Admin/Policy"), getJson("Admin/Users"), getJson("Status")
    ]).then(function (results) {
        STATE.schema = results[0];
        // Unsaved policy edits survive a refresh of the user list.
        if (!STATE.dirty || !STATE.policy) {
            STATE.policy = results[1];
            STATE.saved = clone(results[1]);
        }
        STATE.users = results[2];
        STATE.status = results[3];
        renderAll(view);
        markDirty();
        if (keepTab) { selectTab(view, keepTab); }
        Dashboard.hideLoadingMsg();
    }, showError);
}

function wire(view) {
    view.querySelectorAll(".afTab").forEach(function (tab) {
        tab.addEventListener("click", function () { selectTab(view, tab.dataset.tab); });
        tab.addEventListener("keydown", function (e) {
            if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") { return; }
            var tabs = Array.prototype.slice.call(view.querySelectorAll(".afTab"));
            var next = tabs[(tabs.indexOf(tab) + (e.key === "ArrowRight" ? 1 : tabs.length - 1)) % tabs.length];
            selectTab(view, next.dataset.tab);
            next.focus();
        });
    });
    view.querySelector("#afSettingsSearch").addEventListener("input", function () { filterSettings(view); });
    view.querySelector("#afOnlyChanged").addEventListener("change", function () { filterSettings(view); });
    view.querySelector("#afSavePolicy").addEventListener("click", function () { savePolicy(view); });
    view.querySelector("#afRevert").addEventListener("click", function () {
        STATE.policy = clone(STATE.saved);
        markDirty();
        renderAll(view);
    });
}

export default function (view) {
    view.addEventListener("viewshow", function () {
        if (typeof LibraryMenu !== "undefined" && LibraryMenu.setTabs) { LibraryMenu.setTabs(null); }
        if (!view.dataset.arcfinSyncWired) {
            view.dataset.arcfinSyncWired = "true";
            wire(view);
        }
        var tab = "overview";
        try { tab = sessionStorage.getItem("arcfinSyncTab") || "overview"; } catch (e) { /* private mode */ }
        if (!view.querySelector('.afTab[data-tab="' + tab + '"]')) { tab = "overview"; }
        selectTab(view, tab);
        STATE.dirty = false;
        STATE.policy = null;
        reload(view, tab);
    });
}
