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

// For calls with no body that answer with JSON; send() without a body returns the raw response.
function sendForJson(method, path) {
    return ApiClient.ajax({ type: method, url: api(path), dataType: "json" });
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

function sameId(a, b) {
    return String(a || "").replace(/-/g, "").toLowerCase() === String(b || "").replace(/-/g, "").toLowerCase();
}

// Ticked boxes, one per person, each carrying its user's id.
function peopleChecks(chosen, skip) {
    var node = el("div", { className: "afChecks" });
    STATE.users.forEach(function (user) {
        if (skip && sameId(user.UserId, skip)) { return; }
        var box = el("input", { type: "checkbox" });
        box.checked = (chosen || []).some(function (id) { return sameId(id, user.UserId); });
        box.dataset.userId = user.UserId;
        node.appendChild(el("label", { className: "afInline" }, [box, el("span", { text: user.Name })]));
    });
    return node;
}

function checkedIds(container) {
    var ids = [];
    container.querySelectorAll("input[type=checkbox]").forEach(function (b) { if (b.checked) { ids.push(b.dataset.userId); } });
    return ids;
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
            }),
            securityOn() && !pendingWipe(user.UserId, device.DeviceId) ? el("button", {
                type: "button", className: "afSmallButton afDanger", text: "Wipe This Device", onclick: function () { wipeDevice(view, user, device); }
            }) : null
        ])
    ]);
    wrap.appendChild(head);
    var key = keyRecord(user.UserId, device.DeviceId);
    if (securityOn() && pendingWipe(user.UserId, device.DeviceId) && !(key && key.Locked && key.LockReason === "wipe")) {
        wrap.appendChild(el("div", { className: "afDeviceHead", style: "margin-top:0.35em" }, [
            el("span", { className: "afBadge afBadgeAccent", text: "Wipe pending" }),
            el("span", { className: "afDeviceMeta", text: "Happens the next time it's online." })
        ]));
    }
    if (key) {
        wrap.appendChild(keyStatus(view, key));
    } else if (olderFor(user.UserId).some(function (o) { return o.DeviceId === device.DeviceId; })) {
        wrap.appendChild(el("div", { className: "afDeviceHead", style: "margin-top:0.35em" }, [
            el("span", { className: "afBadge afBadgeAccent", text: "Too old for security features" }),
            el("span", { className: "afDeviceMeta", text: "This Arcfin can't follow key changes or show a lock. Updating it fixes that." })
        ]));
    }
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

function wipeDevice(view, user, device) {
    var name = device.Name || device.JellyfinDeviceName || "this device";
    if (!window.confirm("Wipe " + name + "? Deletes its downloads, locks Arcfin on it and signs it out once it's done. A device that never comes back online is signed out after a week.")) { return; }
    if (window.prompt("Type WIPE to confirm.") !== "WIPE") { return; }
    Dashboard.showLoadingMsg();
    send("POST", "Admin/Security/Devices/Wipe", { UserId: user.UserId, DeviceId: device.DeviceId }).then(function (r) {
        STATE.security = r;
        renderSecurity(view);
        renderUsers(view);
        Dashboard.hideLoadingMsg();
    }, showError);
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

// ----- Key status on Users & Devices -----

function keyRecord(userId, deviceId) {
    var devices = (STATE.security && STATE.security.Devices) || [];
    return devices.filter(function (d) { return d.UserId === userId && d.DeviceId === deviceId; })[0];
}

function securityOn() {
    return !!(STATE.security && STATE.security.Settings && STATE.security.Settings.Enabled);
}

function pendingWipe(userId, deviceId) {
    return ((STATE.security && STATE.security.PendingWipes) || []).some(function (w) { return sameId(w.UserId, userId) && w.DeviceId === deviceId; });
}

function waitingFor(userId) {
    if (!securityOn()) { return []; }
    return ((STATE.security && STATE.security.TrustRequests) || []).filter(function (r) { return sameId(r.UserId, userId); });
}

var LOCK_REASONS = { trust: "Waiting for approval", pinTries: "Locked after too many wrong PINs" };

// Why a locked device is locked, shown only while signed commands are on.
function lockReason(d) {
    if (!securityOn() || !d.Locked || !d.LockReason) { return null; }
    if (d.LockReason === "wipe") { return pendingWipe(d.UserId, d.DeviceId) ? "Wipe pending" : "Wiped"; }
    return LOCK_REASONS[d.LockReason] || null;
}

function answerTrust(view, request, trust) {
    if (!trust && !window.confirm("Don't trust " + (request.DeviceName || "this device") + "? It's signed out and has to sign in again.")) { return; }
    Dashboard.showLoadingMsg();
    send("POST", "Admin/Security/TrustRequests/" + encodeURIComponent(request.Id), { Trust: trust }).then(function (r) {
        STATE.security = r;
        renderSecurity(view);
        renderUsers(view);
        Dashboard.hideLoadingMsg();
    }, function (err) {
        showError(err);
        getJson("Admin/Security").then(function (r) { STATE.security = r; renderSecurity(view); renderUsers(view); });
    });
}

function trustRequestRow(view, request) {
    var meta = ["code " + request.Code, request.DeviceName, relativeTime(request.Created)].filter(Boolean).join(" · ");
    return el("div", { className: "afDevice" }, [
        el("div", { className: "afDeviceHead" }, [
            el("span", { className: "afBadge afBadgeAccent", text: "Waiting for approval" }),
            el("span", { className: "afDeviceMeta", text: meta }),
            el("span", { className: "afDeviceActions" }, [
                el("button", { type: "button", className: "afSmallButton", text: "Trust", onclick: function () { answerTrust(view, request, true); } }),
                el("button", { type: "button", className: "afSmallButton afDanger", text: "Don't Trust", onclick: function () { answerTrust(view, request, false); } })
            ])
        ])
    ]);
}

function keyState(d) {
    return d.MismatchAt ? "Key didn't match" : d.Locked ? "Locked" : d.SignedOut ? "Signed out " + relativeTime(d.SignedOut) : d.UpToDate ? "Key up to date" : "Key behind";
}

// The key status of one Arcfin device: a badge, why it was flagged, and Unlock or Clear.
function keyStatus(view, d) {
    var parts = [];
    var lines = [];
    if (d.AppVersion) { lines.push("Arcfin " + d.AppVersion + (d.Build ? " (" + d.Build + ")" : "")); }
    lines.push("checked in " + relativeTime(d.LastCheckIn));
    if (d.SignOutAfter) { lines.push("signed out " + when(d.SignOutAfter) + " if it's still behind"); }
    var reason = lockReason(d);
    parts.push(el("div", { className: "afDeviceHead", style: "margin-top:0.35em" }, [
        el("span", { className: "afBadge" + (d.Locked || !d.UpToDate || d.MismatchAt ? " afBadgeAccent" : ""), text: keyState(d) }),
        reason ? el("span", { className: "afBadge afBadgeAccent", text: reason }) : null,
        el("span", { className: "afDeviceMeta", text: lines.join(" · ") }),
        d.Locked || d.SignedOut ? el("span", { className: "afDeviceActions" }, [el("button", {
            type: "button", className: "afSmallButton", text: d.Locked ? "Unlock" : "Clear", onclick: function () {
                send("POST", "Admin/Security/Devices/Unlock", { UserId: d.UserId, DeviceId: d.DeviceId })
                    .then(function (r) { STATE.security = r; renderSecurity(view); renderUsers(view); }, showError);
            }
        })]) : null
    ]));
    if (d.MismatchAt) {
        parts.push(el("div", { className: "afDanger", style: "font-size:0.88em;margin-top:0.25em", text: "Reported " + relativeTime(d.MismatchAt) + ": it got a key with fingerprint " + (d.MismatchFingerprint || "?") + ", not this server's. Something on that network may be pretending to be your server." }));
    }
    return el("div", {}, parts);
}

function olderFor(userId) {
    return ((STATE.security && STATE.security.OlderDevices) || []).filter(function (d) { return d.UserId === userId; });
}

function signOutOlder(view, user, deviceId, confirmText) {
    if (!window.confirm(confirmText)) { return; }
    send("POST", "Admin/Security/Devices/SignOut", { UserId: user.UserId, DeviceId: deviceId || "" })
        .then(function (r) { STATE.security = r; renderSecurity(view); renderUsers(view); }, showError);
}

// Older Arcfin sign-ins that never registered as a device: mostly leftovers from old installs.
function olderSignIns(view, user, older) {
    var list = el("div");
    older.forEach(function (d) {
        list.appendChild(el("div", { className: "afDevice" }, [
            el("div", { className: "afDeviceHead" }, [
                el("span", { className: "afDeviceName", text: d.DeviceName || "Arcfin device" }),
                el("span", { className: "afBadge", text: "Arcfin " + d.AppVersion }),
                el("span", { className: "afDeviceMeta", text: d.LastActivity ? "last used " + relativeTime(d.LastActivity) : "" }),
                el("span", { className: "afDeviceActions" }, [el("button", {
                    type: "button", className: "afSmallButton afDanger", text: "Sign Out", onclick: function () {
                        signOutOlder(view, user, d.DeviceId, "Sign out " + (d.DeviceName || "this device") + "? It has to sign in again to use the server.");
                    }
                })])
            ])
        ]));
    });
    list.appendChild(el("div", { className: "afButtons", style: "margin-top:0.5em" }, [el("button", {
        type: "button", className: "afSmallButton afDanger", text: "Sign Out All " + older.length, onclick: function () {
            signOutOlder(view, user, null, "Sign out all " + older.length + " older Arcfin sign-ins for " + user.Name + "? Any still in use have to sign in again.");
        }
    })]));
    return el("details", { className: "afDeviceSettings", style: "margin-top:0.75em" }, [
        el("summary", { text: "Other Arcfin sign-ins (" + older.length + "), too old for security features" }),
        el("div", { className: "afRowMeta", style: "margin:0.4em 0", text: "Sign-ins from older Arcfin versions that never registered here, often left over from old installs. They can't follow key changes or show a lock." }),
        list
    ]);
}

function checkedInOnlyRow(view, d) {
    return el("div", { className: "afDevice" }, [
        el("div", { className: "afDeviceHead" }, [el("span", { className: "afDeviceName", text: d.DeviceName || "Arcfin device" })]),
        keyStatus(view, d)
    ]);
}

// A person's devices that need a look, on their row without opening it.
function userFlags(user) {
    var keys = ((STATE.security && STATE.security.Devices) || []).filter(function (d) { return d.UserId === user.UserId; });
    var registered = user.Devices.map(function (d) { return d.DeviceId; });
    var older = olderFor(user.UserId);
    var tooOld = older.filter(function (d) { return registered.indexOf(d.DeviceId) >= 0; }).length;
    var leftover = older.length - tooOld;
    var waiting = waitingFor(user.UserId);
    // A device waiting for approval is counted once, as waiting.
    var flagged = keys.filter(function (d) {
        if (d.Locked && d.LockReason === "trust" && waiting.some(function (r) { return r.DeviceId === d.DeviceId; })) { return false; }
        return d.MismatchAt || d.Locked || !d.UpToDate;
    }).length;
    var text = [
        waiting.length ? waiting.length + " waiting" : null,
        flagged ? flagged + " need" + (flagged === 1 ? "s" : "") + " a look" : null,
        tooOld ? tooOld + (tooOld === 1 ? " device" : " devices") + " too old" : null,
        leftover ? leftover + " old sign-in" + (leftover === 1 ? "" : "s") : null
    ].filter(Boolean);
    return text.length ? el("span", { className: "afBadge afBadgeAccent", text: text.join(" · ") }) : null;
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
            overrideCount ? el("span", { className: "afBadge", text: overrideCount + (overrideCount === 1 ? " override" : " overrides") }) : null,
            userFlags(user)
        ]);
        var body = el("div", { className: "afUserBody" });
        body.appendChild(el("h4", { className: "afSubTitle", text: "Devices" }));
        var known = user.Devices.map(function (d) { return d.DeviceId; });
        var keyOnly = ((STATE.security && STATE.security.Devices) || []).filter(function (d) { return d.UserId === user.UserId && known.indexOf(d.DeviceId) < 0; });
        var older = olderFor(user.UserId).filter(function (d) { return known.indexOf(d.DeviceId) < 0; });
        var waiting = waitingFor(user.UserId);
        if (user.Devices.length || keyOnly.length || older.length || waiting.length) {
            waiting.forEach(function (r) { body.appendChild(trustRequestRow(view, r)); });
            user.Devices.forEach(function (d) { body.appendChild(deviceRow(view, user, d)); });
            keyOnly.forEach(function (d) { body.appendChild(checkedInOnlyRow(view, d)); });
            if (older.length) { body.appendChild(olderSignIns(view, user, older)); }
        } else {
            body.appendChild(el("div", { className: "afRowMeta", text: "No Arcfin devices yet. A device appears here after it signs in with an Arcfin version that supports devices." }));
        }
        body.appendChild(el("h4", { className: "afSubTitle", text: "Overrides for " + user.Name }));
        body.appendChild(overridesSection(view, user));
        var details = el("details", { className: "afUser", "data-user-id": user.UserId, open: !!open[user.UserId] }, [summary, body]);
        container.appendChild(details);
    });
}


// ----- Pick Together -----

function renderPick(view) {
    var settings = STATE.pick || { Enabled: true, DisabledUsers: [] };
    var enabled = view.querySelector("#afPickEnabled");
    enabled.checked = !!settings.Enabled;
    var list = view.querySelector("#afPickUsers");
    list.textContent = "";
    STATE.users.forEach(function (user) {
        var box = el("input", { type: "checkbox" });
        box.checked = settings.DisabledUsers.indexOf(user.UserId) >= 0;
        box.dataset.userId = user.UserId;
        box.disabled = !enabled.checked;
        list.appendChild(el("label", { className: "afInline", style: "display:flex;margin:0.35em 0" }, [box, el("span", { text: user.Name })]));
    });
    enabled.onchange = function () {
        list.querySelectorAll("input").forEach(function (b) { b.disabled = !enabled.checked; });
    };
}

function savePick(view) {
    var disabled = [];
    view.querySelectorAll("#afPickUsers input").forEach(function (b) { if (b.checked) { disabled.push(b.dataset.userId); } });
    Dashboard.showLoadingMsg();
    send("PUT", "Admin/PickTogether", { Enabled: view.querySelector("#afPickEnabled").checked, DisabledUsers: disabled })
        .then(function (saved) {
            STATE.pick = saved;
            renderPick(view);
            Dashboard.hideLoadingMsg();
            Dashboard.processPluginConfigurationUpdateResult();
        }, function (err) { Dashboard.hideLoadingMsg(); showError(err); });
}

// ----- Watch parties and recommendations -----

function renderAccess(view, settings, enabledId, listId) {
    var enabled = view.querySelector(enabledId);
    enabled.checked = !!settings.Enabled;
    var list = view.querySelector(listId);
    list.textContent = "";
    STATE.users.forEach(function (user) {
        var box = el("input", { type: "checkbox" });
        box.checked = settings.DisabledUsers.indexOf(user.UserId) >= 0;
        box.dataset.userId = user.UserId;
        box.disabled = !enabled.checked;
        list.appendChild(el("label", { className: "afInline", style: "display:flex;margin:0.35em 0" }, [box, el("span", { text: user.Name })]));
    });
    enabled.onchange = function () {
        list.querySelectorAll("input").forEach(function (b) { b.disabled = !enabled.checked; });
    };
}

function saveParty(view) {
    var disabled = [];
    view.querySelectorAll("#afPartyUsers input").forEach(function (b) { if (b.checked) { disabled.push(b.dataset.userId); } });
    Dashboard.showLoadingMsg();
    send("PUT", "Admin/WatchParty", { Enabled: view.querySelector("#afPartyEnabled").checked, DisabledUsers: disabled })
        .then(function (saved) {
            STATE.party = saved;
            renderAccess(view, saved, "#afPartyEnabled", "#afPartyUsers");
            Dashboard.hideLoadingMsg();
            Dashboard.processPluginConfigurationUpdateResult();
        }, showError);
}

function renderRecs(view) {
    var box = view.querySelector("#afRecsEnabled");
    box.checked = !!STATE.recs.Enabled;
    box.onchange = function () {
        send("PUT", "Admin/Recommendations", { Enabled: box.checked }).then(function (saved) { STATE.recs = saved; }, showError);
    };
}

function renderNotifications(view) {
    var box = view.querySelector("#afNotifyEnabled");
    var saved = STATE.notifications || {};
    box.checked = !!saved.Enabled;
    view.querySelector("#afNotifyWebhook").hidden = !(saved.Enabled && saved.SeerrConfigured);
    var address = view.querySelector("#afNotifyAddress");
    var url = view.querySelector("#afNotifyUrl");
    address.value = saved.WebhookAddress || ApiClient.serverAddress();
    var showUrl = function () { url.value = address.value.trim().replace(/\/+$/, "") + (saved.WebhookPath || ""); };
    showUrl();
    address.oninput = showUrl;
    view.querySelector("#afNotifyAddressSave").onclick = function () {
        var typed = address.value.trim();
        send("PUT", "Admin/Notifications", { Enabled: box.checked, WebhookAddress: typed === ApiClient.serverAddress() ? "" : typed }).then(function (result) {
            STATE.notifications = result;
            renderNotifications(view);
            Dashboard.processPluginConfigurationUpdateResult();
        }, showError);
    };
    view.querySelector("#afNotifySecret").value = saved.WebhookSecret || "";
    view.querySelector("#afNotifyPayload").value = saved.SeerrPayload || "";
    box.onchange = function () {
        send("PUT", "Admin/Notifications", { Enabled: box.checked }).then(function (result) {
            STATE.notifications = result;
            renderNotifications(view);
            Dashboard.processPluginConfigurationUpdateResult();
        }, function (err) { box.checked = !box.checked; showError(err); });
    };
    view.querySelector("#afNotifyReset").onclick = function () {
        if (!window.confirm("Make a new secret? Seerr's webhook stops working until you paste the new one into it.")) { return; }
        sendForJson("POST", "Admin/Notifications/Secret/Reset").then(function (result) {
            STATE.notifications = result;
            renderNotifications(view);
        }, showError);
    };
}

function renderHidden(view) {
    var box = view.querySelector("#afHiddenEnabled");
    box.checked = !!(STATE.hidden && STATE.hidden.Enabled);
    box.onchange = function () {
        send("PUT", "Admin/Hidden", { Enabled: box.checked }).then(function (saved) {
            STATE.hidden = saved;
            box.checked = !!saved.Enabled;
            Dashboard.processPluginConfigurationUpdateResult();
        }, function (err) { box.checked = !box.checked; showError(err); });
    };
}

// ----- Seerr & TMDB -----

function userName(id) {
    var user = STATE.users.filter(function (u) { return u.UserId === id; })[0];
    return user ? user.Name : "Someone";
}

var SEERR_STATES = {
    ok: "Seerr answered.",
    keyRejected: "Seerr refused the key. Check it in Seerr › Settings › General.",
    unreachable: "This server can't reach Seerr at that address.",
    error: "Seerr had a problem."
};

var VAULT_SOURCES = {
    environment: "The master key comes from the ARCFIN_SYNC_MASTER_KEY environment variable, outside Jellyfin's folders: the strongest setup.",
    file: "The master key comes from the file named by ARCFIN_SYNC_MASTER_KEY_FILE, outside Jellyfin's folders: the strongest setup.",
    generated: "Keys are encrypted with AES-256-GCM using a master key the plugin made in its own data folder, readable by the server's account only. For more protection, keep the master key outside Jellyfin's folders: set ARCFIN_SYNC_MASTER_KEY to a 32-byte key in base64 (openssl rand -base64 32), or ARCFIN_SYNC_MASTER_KEY_FILE to a file holding one (e.g. a Docker secret), then save the keys again."
};

function renderIntegrations(view) {
    var data = STATE.integrations;
    view.querySelector("#afSeerrUrl").value = data.SeerrUrl || "";
    view.querySelector("#afSeerrPublicUrl").value = data.SeerrPublicUrl || "";
    view.querySelector("#afSeerrKey").value = "";
    view.querySelector("#afTmdbKey").value = "";
    view.querySelector("#afSeerrAddUsers").checked = !!data.AddSeerrUsers;
    view.querySelector("#afSeerrKeyState").textContent = data.SeerrKeySet ? "Saved" : "Not set";
    view.querySelector("#afTmdbKeyState").textContent = data.TmdbKeySet ? "Saved" : "Not set";
    view.querySelector("#afSeerrRemove").hidden = !data.SeerrKeySet;
    view.querySelector("#afTmdbRemove").hidden = !data.TmdbKeySet;
    var health = view.querySelector("#afSeerrHealth");
    health.textContent = data.SeerrState
        ? (SEERR_STATES[data.SeerrState] || data.SeerrState) + (data.SeerrMessage && data.SeerrState !== "ok" ? " (" + data.SeerrMessage + ")" : "") + " " + relativeTime(data.SeerrChecked)
        : "";
    var queue = view.querySelector("#afQueue");
    queue.textContent = "";
    if (!data.Queue.length) {
        queue.appendChild(el("div", { className: "afEmpty", style: "padding:0.5em 0", text: "Nothing waiting." }));
    }
    data.Queue.forEach(function (q) {
        queue.appendChild(el("div", { className: "afDevice" }, [
            el("div", { className: "afDeviceHead" }, [
                el("span", { className: "afDeviceName", text: userName(q.UserId) }),
                el("span", { className: "afDeviceMeta", text: relativeTime(q.Created) + " · tried " + q.Attempts + "×" + (q.LastError ? " · " + q.LastError : "") }),
                el("span", { className: "afDeviceActions" }, [el("button", {
                    type: "button", className: "afSmallButton afDanger", text: "Drop", onclick: function () {
                        sendForJson("DELETE", "Admin/Integrations/Queue/" + q.Id).then(function (d) { STATE.integrations = d; renderIntegrations(view); }, showError);
                    }
                })])
            ])
        ]));
    });
    view.querySelector("#afQueueSend").hidden = !data.Queue.length;
    view.querySelector("#afVaultInfo").textContent = VAULT_SOURCES[data.VaultKeySource] || "";
}

// Seerr's card saves only Seerr, and TMDB's only TMDB: the other card's typed or saved values
// are left as they are on the server (a null key keeps the saved one).
function saveIntegrations(view, part, overrides) {
    var saved = STATE.integrations;
    var seerr = part === "seerr";
    var body = {
        SeerrUrl: seerr ? view.querySelector("#afSeerrUrl").value : saved.SeerrUrl,
        SeerrPublicUrl: seerr ? view.querySelector("#afSeerrPublicUrl").value : saved.SeerrPublicUrl,
        SeerrApiKey: seerr ? (view.querySelector("#afSeerrKey").value || null) : null,
        TmdbKey: seerr ? null : (view.querySelector("#afTmdbKey").value || null),
        AddSeerrUsers: seerr ? view.querySelector("#afSeerrAddUsers").checked : saved.AddSeerrUsers
    };
    Object.keys(overrides || {}).forEach(function (k) { body[k] = overrides[k]; });
    Dashboard.showLoadingMsg();
    var otherCard = seerr
        ? { tmdb: view.querySelector("#afTmdbKey").value }
        : { url: view.querySelector("#afSeerrUrl").value, publicUrl: view.querySelector("#afSeerrPublicUrl").value,
            key: view.querySelector("#afSeerrKey").value, addUsers: view.querySelector("#afSeerrAddUsers").checked };
    return send("PUT", "Admin/Integrations", body).then(function (result) {
        STATE.integrations = result;
        renderIntegrations(view);
        // What's typed in the other card and not saved yet stays typed.
        if (seerr) {
            view.querySelector("#afTmdbKey").value = otherCard.tmdb;
        } else {
            view.querySelector("#afSeerrUrl").value = otherCard.url;
            view.querySelector("#afSeerrPublicUrl").value = otherCard.publicUrl;
            view.querySelector("#afSeerrKey").value = otherCard.key;
            view.querySelector("#afSeerrAddUsers").checked = otherCard.addUsers;
        }
        Dashboard.hideLoadingMsg();
        Dashboard.processPluginConfigurationUpdateResult();
    }, showError);
}

function integrationAction(view, method, path, message, body) {
    Dashboard.showLoadingMsg();
    (body === undefined ? sendForJson(method, path) : send(method, path, body)).then(function (data) {
        STATE.integrations = data;
        renderIntegrations(view);
        Dashboard.hideLoadingMsg();
        if (message) { Dashboard.alert({ title: "Arcfin Sync", message: message(data) }); }
    }, showError);
}

// ----- Security -----

var COMMAND_NAMES = { resetPin: "PIN cleared", lock: "Locked", unlock: "Unlocked" };

function renderSecurity(view) {
    var data = STATE.security;
    var info = view.querySelector("#afKeyInfo");
    info.textContent = "";
    if (data.Key) {
        info.appendChild(el("div", { className: "afMono", style: "font-size:1.4em;font-weight:600;letter-spacing:0.04em;margin:0.2em 0", text: data.Key.Fingerprint }));
        info.appendChild(el("div", { text: "Fingerprint · made " + relativeTime(data.Key.Created) + ". A device reaching this server over plain HTTP shows this in Settings › Server; check it matches before confirming." }));
        info.appendChild(el("div", { text: "Devices that already know this server move to a new key on their own: the old key signs the new one." }));
    } else {
        info.textContent = data.Settings && data.Settings.Enabled
            ? "No key yet. Make one to send commands."
            : "No key yet. Turn on Signed Commands above and one is made.";
    }
    view.querySelector("#afKeyGenerate").querySelector("span").textContent = data.Key ? "Make a New Key" : "Make a Key";
    var list = view.querySelector("#afSecurityUsers");
    list.textContent = "";
    STATE.users.forEach(function (user) {
        var locked = data.LockedUsers.indexOf(user.UserId) >= 0;
        var last = data.Commands.filter(function (c) { return c.UserId === user.UserId; })[0];
        var command = function (kind, confirmText) {
            return function () {
                if (!data.Key) { Dashboard.alert({ title: "Arcfin Sync", message: "Make or add a signing key first." }); return; }
                if (!window.confirm(confirmText)) { return; }
                send("POST", "Admin/Security/Commands", { UserId: user.UserId, Kind: kind })
                    .then(function (d) { STATE.security = d; renderSecurity(view); }, showError);
            };
        };
        var accountOff = (data.DisabledUsers || []).indexOf(user.UserId) >= 0;
        list.appendChild(el("div", { className: "afDevice" }, [
            el("div", { className: "afDeviceHead" }, [
                el("span", { className: "afDeviceName", text: user.Name }),
                locked ? el("span", { className: "afBadge afBadgeAccent", text: "Locked" }) : null,
                accountOff ? el("span", { className: "afBadge afBadgeAccent", text: "Account disabled" }) : null,
                el("span", { className: "afDeviceMeta", text: last ? (COMMAND_NAMES[last.Kind] || last.Kind) : "" }),
                el("span", { className: "afDeviceActions" }, [
                    el("button", { type: "button", className: "afSmallButton", text: "Clear PIN", onclick: command("resetPin", "Clear " + user.Name + "'s PIN on all their devices? They can set a new one in Arcfin.") }),
                    locked
                        ? el("button", { type: "button", className: "afSmallButton", text: "Unlock", onclick: command("unlock", "Unlock " + user.Name + " on all their devices?") })
                        : el("button", { type: "button", className: "afSmallButton afDanger", text: "Lock", onclick: function () {
                            if (!data.Key) { Dashboard.alert({ title: "Arcfin Sync", message: "Make or add a signing key first." }); return; }
                            if (!window.confirm("Lock " + user.Name + " on all their devices? Arcfin won't open this profile until you unlock it. Older Arcfin versions can't show a lock, so they're signed out.")) { return; }
                            var disable = window.confirm("Also disable " + user.Name + "'s Jellyfin account? They're signed out everywhere and can't use the server at all, in any app, until you unlock them here.\n\nOK disables the account; Cancel only locks Arcfin.");
                            send("POST", "Admin/Security/Commands", { UserId: user.UserId, Kind: "lock", DisableAccount: disable })
                                .then(function (d) { STATE.security = d; renderSecurity(view); }, showError);
                        } })
                ])
            ])
        ]));
    });
    renderSecuritySettings(view);
}

var TRUST_CHOICES = { inherit: "Same as everyone", on: "On", off: "Off" };

var STALE_ACTIONS = { inherit: "Same as everyone", off: "Off", signOut: "Sign them out", lock: "Sign them out and lock Arcfin" };

function renderSecuritySettings(view) {
    var data = STATE.security;
    var settings = data.Settings;
    view.querySelector("#afTrustOverHttp").checked = !!settings.TrustOverHttp;
    view.querySelector("#afRotate").checked = !!settings.AutoRotate;
    view.querySelector("#afRotate").disabled = !!(data.Key && data.Key.Imported);
    view.querySelector("#afRotateDays").value = settings.RotateEveryDays;
    view.querySelector("#afRotateNext").textContent = data.Key && data.Key.Imported
        ? "Your own key is in use, so it isn't rotated. Make a key here to turn rotation on."
        : data.NextRotation ? "Next new key: " + when(data.NextRotation) + "." : "";
    view.querySelector("#afStaleAction").value = settings.StaleAction;
    view.querySelector("#afStaleDays").value = settings.StaleAfterDays;
    view.querySelector("#afOlderApps").value = settings.OlderApps || "allow";
    view.querySelector("#afSecurityEnabled").checked = !!settings.Enabled;
    view.querySelector("#afTrustRequired").checked = !!settings.RequireTrustedDevices;
    view.querySelector("#afHttpsAway").checked = !!settings.RequireHttpsAway;
    var pins = settings.Pins || { RequiredFor: "none", Users: [], MinLength: 4, MaxTries: null };
    var required = view.querySelector("#afPinRequired");
    required.value = pins.RequiredFor || "none";
    view.querySelector("#afPinMin").value = String(pins.MinLength || 4);
    view.querySelector("#afPinTries").value = pins.MaxTries == null ? "" : String(pins.MaxTries);
    var pinUsers = view.querySelector("#afPinUsers");
    pinUsers.textContent = "";
    pinUsers.appendChild(peopleChecks(pins.Users || []));
    pinUsers.hidden = required.value !== "chosen";
    required.onchange = function () { pinUsers.hidden = required.value !== "chosen"; };
    var trustList = view.querySelector("#afTrustUsers");
    trustList.textContent = "";
    STATE.users.forEach(function (user) {
        var own = (settings.Users || []).filter(function (u) { return sameId(u.UserId, user.UserId); })[0];
        var choice = el("select", { className: "afControl", style: "width:auto", "aria-label": "New devices need approving for " + user.Name });
        Object.keys(TRUST_CHOICES).forEach(function (k) { choice.appendChild(el("option", { value: k, text: TRUST_CHOICES[k] })); });
        choice.value = own && own.TrustedDevices ? own.TrustedDevices : "inherit";
        var row = el("div", { className: "afRename", style: "align-items:center" }, [el("span", { className: "afUserName", style: "min-width:9em", text: user.Name }), choice]);
        row.dataset.userId = user.UserId;
        row.choice = choice;
        trustList.appendChild(row);
    });
    var list = view.querySelector("#afStaleUsers");
    list.textContent = "";
    STATE.users.forEach(function (user) {
        var own = (settings.Users || []).filter(function (u) { return u.UserId === user.UserId; })[0];
        var action = el("select", { className: "afControl", style: "width:auto" });
        Object.keys(STALE_ACTIONS).forEach(function (k) { var o = el("option", { value: k, text: STALE_ACTIONS[k] }); action.appendChild(o); });
        action.value = own ? own.StaleAction : "inherit";
        var days = el("input", { type: "number", className: "afControl", min: "1", max: "3650", placeholder: "Same", value: own && own.StaleAfterDays != null ? String(own.StaleAfterDays) : "", style: "width:5em;height:2.4em" });
        var row = el("div", { className: "afRename", style: "align-items:center" }, [el("span", { className: "afUserName", style: "min-width:9em", text: user.Name }), action, el("span", { text: "after" }), days, el("span", { text: "days" })]);
        row.collect = function () {
            var d = days.value === "" ? null : parseInt(days.value, 10);
            return action.value === "inherit" && d == null ? null : { UserId: user.UserId, StaleAction: action.value, StaleAfterDays: d };
        };
        list.appendChild(row);
    });
}

// The server replaces the whole SecuritySettings, so anything this page doesn't show keeps its loaded value.
function saveSecuritySettings(view) {
    var loaded = STATE.security.Settings;
    var body = clone(loaded);
    var byUser = {};
    var entry = function (id) {
        return byUser[id] || (byUser[id] = { UserId: id, StaleAction: "inherit", StaleAfterDays: null, TrustedDevices: "inherit" });
    };
    view.querySelectorAll("#afStaleUsers .afRename").forEach(function (row) {
        var u = row.collect();
        if (u) { var e = entry(u.UserId); e.StaleAction = u.StaleAction; e.StaleAfterDays = u.StaleAfterDays; }
    });
    view.querySelectorAll("#afTrustUsers .afRename").forEach(function (row) {
        if (row.choice.value !== "inherit") { entry(row.dataset.userId).TrustedDevices = row.choice.value; }
    });
    var tries = view.querySelector("#afPinTries").value;
    body.Enabled = view.querySelector("#afSecurityEnabled").checked;
    body.TrustOverHttp = view.querySelector("#afTrustOverHttp").checked;
    body.AutoRotate = view.querySelector("#afRotate").checked;
    body.RotateEveryDays = parseInt(view.querySelector("#afRotateDays").value, 10) || 1;
    body.StaleAction = view.querySelector("#afStaleAction").value;
    body.StaleAfterDays = parseInt(view.querySelector("#afStaleDays").value, 10) || loaded.StaleAfterDays || 14;
    body.OlderApps = view.querySelector("#afOlderApps").value;
    body.RequireTrustedDevices = view.querySelector("#afTrustRequired").checked;
    body.RequireHttpsAway = view.querySelector("#afHttpsAway").checked;
    body.Pins = {
        RequiredFor: view.querySelector("#afPinRequired").value,
        Users: checkedIds(view.querySelector("#afPinUsers")),
        MinLength: parseInt(view.querySelector("#afPinMin").value, 10) || 4,
        MaxTries: tries === "" ? null : parseInt(tries, 10)
    };
    body.Users = Object.keys(byUser).map(function (k) { return byUser[k]; });
    var turnedOn = body.Enabled && !loaded.Enabled;
    send("PUT", "Admin/Security/Settings", body).then(function (d) {
        // Only the GET makes the signing key, so fetch again once signed commands are on.
        return turnedOn ? getJson("Admin/Security") : d;
    }).then(function (d) {
        STATE.security = d;
        renderSecurity(view);
        renderUsers(view);
        Dashboard.processPluginConfigurationUpdateResult();
    }, function (err) {
        showError(err);
        renderSecuritySettings(view);
    });
}

function makeKey(view, pem) {
    var warn = pem && STATE.security.Settings.AutoRotate
        ? "Use your own key? Automatic rotation only works with keys the plugin makes, so it turns off. Devices move to your key automatically."
        : STATE.security.Key ? "Make a new signing key? Devices move to it automatically." : null;
    if (warn && !window.confirm(warn)) { return; }
    Dashboard.showLoadingMsg();
    send("POST", "Admin/Security/Key", { Pem: pem || null }).then(function (d) {
        STATE.security = d;
        view.querySelector("#afKeyPem").value = "";
        renderSecurity(view);
        Dashboard.hideLoadingMsg();
    }, showError);
}

// ----- Watch time -----

function limitRule(userId) {
    return STATE.limits.Rules.filter(function (r) { return r.UserId === userId; })[0];
}

function renderLimits(view) {
    var data = STATE.limits;
    view.querySelector("#afLimitsZone").value = data.TimeZone || "";
    view.querySelector("#afLimitsWarn").checked = !!data.WarnBeforeEnd;
    view.querySelector("#afLimitsClock").textContent = "Server time now: " + data.ServerTime + (data.TimeZone ? "" : " (" + data.ServerTimeZone + ")");
    var list = view.querySelector("#afLimitsUsers");
    list.textContent = "";
    STATE.users.forEach(function (user) {
        var rule = limitRule(user.UserId);
        var on = el("input", { type: "checkbox" });
        on.checked = !!rule;
        var field = function (label, value, attrs) {
            var input = el("input", Object.assign({ className: "afControl", value: value == null ? "" : String(value) }, attrs));
            return { input: input, node: el("label", { className: "afLimitField" }, [el("span", { className: "afRowMeta", text: label }), input]) };
        };
        var daily = field("Minutes a day (0 = no limit)", rule ? rule.DailyMinutes : 120, { type: "number", min: "0", max: "1440" });
        var weekend = field("Weekend minutes (empty = same)", rule && rule.WeekendMinutes != null ? rule.WeekendMinutes : "", { type: "number", min: "0", max: "1440" });
        var start = field("Bedtime starts", rule && rule.BedtimeStart ? rule.BedtimeStart : "", { type: "time" });
        var end = field("Bedtime ends", rule && rule.BedtimeEnd ? rule.BedtimeEnd : "", { type: "time" });
        var zone = field("Time zone (empty = server's)", rule && rule.TimeZone ? rule.TimeZone : "", { type: "text", placeholder: data.TimeZone || data.ServerTimeZone });
        var used = data.UsedToday[user.UserId];
        var body = el("div", { className: "afLimitGrid" }, [daily.node, weekend.node, start.node, end.node, zone.node]);
        body.hidden = !on.checked;
        on.addEventListener("change", function () { body.hidden = !on.checked; });
        var card = el("div", { className: "afCard" }, [
            el("label", { className: "afInline" }, [on, el("span", { className: "afUserName", text: user.Name })]),
            used != null ? el("span", { className: "afDeviceMeta", text: "  · " + used + " min watched today" }) : null,
            body
        ]);
        card.collect = function () {
            if (!on.checked) { return null; }
            return {
                UserId: user.UserId,
                DailyMinutes: parseInt(daily.input.value, 10) || 0,
                WeekendMinutes: weekend.input.value === "" ? null : parseInt(weekend.input.value, 10),
                BedtimeStart: start.input.value || null,
                BedtimeEnd: end.input.value || null,
                TimeZone: zone.input.value.trim() || null
            };
        };
        list.appendChild(card);
    });
}

function saveLimits(view) {
    var rules = [];
    view.querySelectorAll("#afLimitsUsers .afCard").forEach(function (card) { var r = card.collect(); if (r) { rules.push(r); } });
    Dashboard.showLoadingMsg();
    send("PUT", "Admin/Limits", { TimeZone: view.querySelector("#afLimitsZone").value || null, WarnBeforeEnd: view.querySelector("#afLimitsWarn").checked, Rules: rules }).then(function (d) {
        STATE.limits = d;
        renderLimits(view);
        Dashboard.hideLoadingMsg();
        Dashboard.processPluginConfigurationUpdateResult();
    }, showError);
}

// ----- Happening now and history -----

var PHASES = { scheduled: "Scheduled", lobby: "In the lobby", starting: "Starting", started: "Watching" };
var OUTCOMES = { watched: "Watched together", ended: "Ended by the host", cancelled: "Ended by an admin", notStarted: "Never started", left: "Everyone left" };

function when(iso) {
    var d = new Date(iso);
    return isNaN(d.getTime()) ? "" : d.toLocaleString([], { weekday: "short", day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
}

function toLocalInput(iso) {
    var d = iso ? new Date(iso) : new Date(Date.now() + 3600000);
    var pad = function (n) { return String(n).padStart(2, "0"); };
    return d.getFullYear() + "-" + pad(d.getMonth() + 1) + "-" + pad(d.getDate()) + "T" + pad(d.getHours()) + ":" + pad(d.getMinutes());
}

function liveAction(view, method, path, body, confirmText) {
    if (confirmText && !window.confirm(confirmText)) { return; }
    var call = body === undefined ? sendForJson(method, path) : send(method, path, body);
    call.then(function (live) { STATE.live = live; renderLive(view); }, function (err) { showError(err); loadLive(view); });
}

function peopleList(view, base, item, people, canRemove) {
    var list = el("div", { className: "afKv", style: "grid-template-columns:minmax(0,1fr) auto" });
    people.forEach(function (p) {
        var detail = [p.IsHost ? "Host" : null, p.DeviceName, p.IsAway ? "Away" : null].filter(Boolean).join(" · ");
        list.appendChild(el("div", { text: p.Name + (detail ? " (" + detail + ")" : "") }));
        list.appendChild(canRemove && !p.IsHost ? el("button", {
            type: "button", className: "afSmallButton afDanger", text: "Remove", onclick: function () {
                liveAction(view, "DELETE", base + "/" + item.Code + "/People/" + p.Id, undefined, "Remove " + p.Name + "? They can't join again with this code.");
            }
        }) : el("div"));
    });
    return list;
}

function renderLive(view) {
    var holder = view.querySelector("#afLive");
    holder.textContent = "";
    var live = STATE.live || { WatchParties: [], PickSessions: [] };
    if (!live.WatchParties.length && !live.PickSessions.length) {
        holder.appendChild(el("div", { className: "afEmpty", style: "padding:0.5em 0", text: "Nothing on right now." }));
    }
    live.WatchParties.forEach(function (party) {
        var timeBox = el("input", { type: "datetime-local", className: "afControl", value: toLocalInput(party.ScheduledFor), style: "width:auto;height:2.4em" });
        var canMove = party.Phase === "scheduled" || party.Phase === "lobby";
        holder.appendChild(el("div", { className: "afDevice" }, [
            el("div", { className: "afDeviceHead" }, [
                el("span", { className: "afDeviceName", text: "Watch party · " + party.ItemName }),
                el("span", { className: "afBadge", text: PHASES[party.Phase] || party.Phase }),
                party.Locked ? el("span", { className: "afBadge", text: "Locked" }) : null,
                el("span", { className: "afDeviceMeta", text: "Code " + party.Code + (party.ScheduledFor ? " · starts " + when(party.ScheduledFor) : "") })
            ]),
            peopleList(view, "Admin/WatchParties", party, party.People, true),
            el("div", { className: "afRename" }, [
                canMove ? timeBox : null,
                canMove ? el("button", { type: "button", className: "afSmallButton", text: party.ScheduledFor ? "Change Time" : "Schedule", onclick: function () {
                    if (!timeBox.value) { return; }
                    liveAction(view, "POST", "Admin/WatchParties/" + party.Code + "/Schedule", { ScheduledFor: new Date(timeBox.value).toISOString() });
                } }) : null,
                canMove && party.ScheduledFor ? el("button", { type: "button", className: "afSmallButton", text: "Make It Now", onclick: function () {
                    liveAction(view, "POST", "Admin/WatchParties/" + party.Code + "/Schedule", { ScheduledFor: null });
                } }) : null,
                el("button", { type: "button", className: "afSmallButton", text: party.Locked ? "Unlock" : "Lock", onclick: function () {
                    liveAction(view, "POST", "Admin/WatchParties/" + party.Code + "/Lock", { Locked: !party.Locked });
                } }),
                el("button", { type: "button", className: "afSmallButton afDanger", text: "End", onclick: function () {
                    liveAction(view, "POST", "Admin/WatchParties/" + party.Code + "/End", undefined, "End this watch party for everyone in it?");
                } })
            ])
        ]));
    });
    live.PickSessions.forEach(function (session) {
        var matches = session.Matches.map(function (m) { return m.Name; }).join(", ");
        holder.appendChild(el("div", { className: "afDevice" }, [
            el("div", { className: "afDeviceHead" }, [
                el("span", { className: "afDeviceName", text: "Pick Together · round " + session.Round }),
                session.WatchingTogether ? el("span", { className: "afBadge", text: "Watching " + session.WatchingTogether }) : null,
                session.Locked ? el("span", { className: "afBadge", text: "Locked" }) : null,
                el("span", { className: "afDeviceMeta", text: "Code " + session.Code + " · started " + relativeTime(session.Created) + (matches ? " · matches: " + matches : "") })
            ]),
            peopleList(view, "Admin/PickSessions", session, session.People, true),
            el("div", { className: "afRename" }, [
                el("button", { type: "button", className: "afSmallButton", text: session.Locked ? "Unlock" : "Lock", onclick: function () {
                    liveAction(view, "POST", "Admin/PickSessions/" + session.Code + "/Lock", { Locked: !session.Locked });
                } }),
                el("button", { type: "button", className: "afSmallButton afDanger", text: "End", onclick: function () {
                    liveAction(view, "POST", "Admin/PickSessions/" + session.Code + "/End", undefined, "End this Pick Together session for everyone in it?");
                } })
            ])
        ]));
    });
}

function loadLive(view) {
    return getJson("Admin/Live").then(function (live) { STATE.live = live; renderLive(view); }, showError);
}

function renderHistorySettings(view) {
    var settings = STATE.historySettings;
    view.querySelector("#afHistoryEnabled").checked = !!settings.Enabled;
    var select = view.querySelector("#afHistoryKeep");
    var custom = view.querySelector("#afHistoryCustom");
    var preset = settings.KeepDays == null ? "never" : String(settings.KeepDays);
    var known = Array.prototype.some.call(select.options, function (o) { return o.value === preset; });
    select.value = known ? preset : "custom";
    custom.value = settings.KeepDays == null ? "" : settings.KeepDays;
    view.querySelector("#afHistoryCustomWrap").hidden = select.value !== "custom";
}

function saveHistorySettings(view) {
    var select = view.querySelector("#afHistoryKeep").value;
    var days = select === "never" ? null : select === "custom" ? parseInt(view.querySelector("#afHistoryCustom").value, 10) : parseInt(select, 10);
    if (select === "custom" && !(days >= 1 && days <= 3650)) {
        Dashboard.alert({ title: "Arcfin Sync", message: "Pick 1 to 3650 days." });
        return;
    }
    send("PUT", "Admin/History/Settings", { Enabled: view.querySelector("#afHistoryEnabled").checked, KeepDays: days }).then(function (saved) {
        STATE.historySettings = saved;
        renderHistorySettings(view);
        loadHistory(view);
        Dashboard.processPluginConfigurationUpdateResult();
    }, showError);
}

function loadHistory(view) {
    var kind = view.querySelector("#afHistoryKind").value;
    return getJson("Admin/History" + (kind ? "?kind=" + kind : "")).then(function (entries) {
        var holder = view.querySelector("#afHistory");
        holder.textContent = "";
        if (!entries.length) {
            holder.appendChild(el("div", { className: "afEmpty", style: "padding:0.5em 0", text: STATE.historySettings.Enabled ? "Nothing yet." : "History is off." }));
        }
        entries.forEach(function (e) {
            var title = e.Kind === "watchParty" ? "Watch party · " + (e.ItemName || "") : "Pick Together" + (e.ItemName ? " · watched " + e.ItemName : "");
            var lines = [
                "Hosted by " + (e.HostName || "someone") + " · " + (e.People || []).join(", "),
                (e.ScheduledFor ? "Planned for " + when(e.ScheduledFor) + " · " : "") + (e.StartedWatching ? "Started " + when(e.StartedWatching) + " · " : "") + "Ended " + when(e.Ended)
            ];
            if (e.Matches && e.Matches.length) { lines.push("Matches: " + e.Matches.join(", ")); }
            holder.appendChild(el("div", { className: "afDevice" }, [
                el("div", { className: "afDeviceHead" }, [
                    el("span", { className: "afDeviceName", text: title }),
                    el("span", { className: "afBadge", text: OUTCOMES[e.Outcome] || e.Outcome })
                ]),
                el("div", { className: "afDeviceMeta", text: lines.join("\n"), style: "white-space:pre-line" })
            ]));
        });
    }, showError);
}

// ----- Household -----

function labeled(text, control) {
    return el("label", { className: "afLimitField" }, [el("span", { className: "afRowMeta", text: text }), control]);
}

function numberOrNull(input, decimal) {
    var v = input.value.trim();
    if (v === "") { return null; }
    var n = decimal ? parseFloat(v) : parseInt(v, 10);
    return isNaN(n) ? null : n;
}

function formatGb(bytes) {
    var gb = (bytes || 0) / 1000000000;
    return (gb >= 10 ? gb.toFixed(0) : gb.toFixed(1)) + " GB";
}

function downloadUse(userId) {
    var use = (STATE.household && STATE.household.DownloadUse) || {};
    var key = Object.keys(use).filter(function (k) { return sameId(k, userId); })[0];
    return key ? use[key] : { Titles: 0, Bytes: 0 };
}

// "" for an empty field; otherwise the ISO time of a datetime-local value.
function fromLocalInput(value) {
    if (!value) { return null; }
    var d = new Date(value);
    return isNaN(d.getTime()) ? null : d.toISOString();
}

function optionalLocalInput(iso) {
    return iso ? toLocalInput(iso) : "";
}

function saveHousehold(view, part, body, draw) {
    Dashboard.showLoadingMsg();
    return send("PUT", "Admin/Household/" + part, body).then(function (d) {
        STATE.household = d;
        draw(view);
        Dashboard.hideLoadingMsg();
        Dashboard.processPluginConfigurationUpdateResult();
    }, showError);
}

var LIMIT_MODES = [["inherit", "Same as everyone"], ["off", "No limits"], ["custom", "Custom"]];

function capInput(value, max, decimal) {
    var input = el("input", {
        type: "number", className: "afControl", min: decimal ? "0.1" : "1", max: max, step: decimal ? "0.1" : "1",
        inputmode: decimal ? "decimal" : "numeric", placeholder: "No cap"
    });
    input.value = value == null ? "" : String(value);
    return input;
}

// One kind of limit for one person: a mode, and their own caps when it's custom.
function limitChoice(label, mode, caps) {
    var select = el("select", { className: "afControl", style: "width:auto;height:2.4em", "aria-label": label });
    LIMIT_MODES.forEach(function (o) { select.appendChild(el("option", { value: o[0], text: o[1] })); });
    select.value = mode || "inherit";
    var grid = el("div", { className: "afLimitGrid" }, caps.map(function (c) { return labeled(c[0], c[1]); }));
    grid.hidden = select.value !== "custom";
    select.onchange = function () { grid.hidden = select.value !== "custom"; };
    return { select: select, grid: grid };
}

function renderDownloads(view) {
    var d = STATE.household.Downloads || { Users: [] };
    view.querySelector("#afDlEnabled").checked = !!d.Enabled;
    view.querySelector("#afDlTitles").value = d.MaxTitles == null ? "" : String(d.MaxTitles);
    view.querySelector("#afDlGb").value = d.MaxGigabytes == null ? "" : String(d.MaxGigabytes);
    view.querySelector("#afDlMusicEnabled").checked = !!d.MusicEnabled;
    view.querySelector("#afDlAlbums").value = d.MusicMaxAlbums == null ? "" : String(d.MusicMaxAlbums);
    view.querySelector("#afDlSongs").value = d.MusicMaxSongs == null ? "" : String(d.MusicMaxSongs);
    view.querySelector("#afDlMusicGb").value = d.MusicMaxGigabytes == null ? "" : String(d.MusicMaxGigabytes);
    var list = view.querySelector("#afDlUsers");
    list.textContent = "";
    STATE.users.forEach(function (user) {
        var own = (d.Users || []).filter(function (u) { return sameId(u.UserId, user.UserId); })[0] || {};
        var titles = capInput(own.MaxTitles, "100000");
        var gb = capInput(own.MaxGigabytes, "100000", true);
        var albums = capInput(own.MusicMaxAlbums, "100000");
        var songs = capInput(own.MusicMaxSongs, "1000000");
        var musicGb = capInput(own.MusicMaxGigabytes, "100000", true);
        var video = limitChoice("Movie and episode limits for " + user.Name, own.Mode, [["Titles (empty = no cap)", titles], ["GB (empty = no cap)", gb]]);
        var music = limitChoice("Music limits for " + user.Name, own.MusicMode, [["Albums (empty = no cap)", albums], ["Songs (empty = no cap)", songs], ["GB (empty = no cap)", musicGb]]);
        var use = downloadUse(user.UserId);
        var row = el("div", { className: "afDevice" }, [
            el("div", { className: "afDeviceName", text: user.Name }),
            el("div", { className: "afDeviceHead", style: "margin-top:0.35em" }, [
                el("span", { className: "afRowMeta", style: "min-width:11em", text: "Movies and episodes" }),
                el("span", { className: "afDeviceMeta", text: (use.Titles || 0) + (use.Titles === 1 ? " title" : " titles") + " · " + formatGb(use.Bytes) }),
                el("span", { className: "afDeviceActions" }, [video.select])
            ]),
            video.grid,
            el("div", { className: "afDeviceHead", style: "margin-top:0.35em" }, [
                el("span", { className: "afRowMeta", style: "min-width:11em", text: "Music" }),
                el("span", { className: "afDeviceMeta", text: (use.Albums || 0) + (use.Albums === 1 ? " album" : " albums") + " · " + (use.Songs || 0) + (use.Songs === 1 ? " song" : " songs") + " · " + formatGb(use.MusicBytes) }),
                el("span", { className: "afDeviceActions" }, [music.select])
            ]),
            music.grid
        ]);
        row.collect = function () {
            var v = video.select.value;
            var m = music.select.value;
            if (v === "inherit" && m === "inherit") { return null; }
            return {
                UserId: user.UserId,
                Mode: v,
                MaxTitles: v === "custom" ? numberOrNull(titles) : null,
                MaxGigabytes: v === "custom" ? numberOrNull(gb, true) : null,
                MusicMode: m,
                MusicMaxAlbums: m === "custom" ? numberOrNull(albums) : null,
                MusicMaxSongs: m === "custom" ? numberOrNull(songs) : null,
                MusicMaxGigabytes: m === "custom" ? numberOrNull(musicGb, true) : null
            };
        };
        list.appendChild(row);
    });
}

// The server replaces the whole DownloadLimitSettings, so anything not shown keeps its loaded value.
function saveDownloads(view) {
    var body = clone(STATE.household.Downloads || {});
    var users = [];
    view.querySelectorAll("#afDlUsers .afDevice").forEach(function (row) { var u = row.collect(); if (u) { users.push(u); } });
    body.Enabled = view.querySelector("#afDlEnabled").checked;
    body.MaxTitles = numberOrNull(view.querySelector("#afDlTitles"));
    body.MaxGigabytes = numberOrNull(view.querySelector("#afDlGb"), true);
    body.MusicEnabled = view.querySelector("#afDlMusicEnabled").checked;
    body.MusicMaxAlbums = numberOrNull(view.querySelector("#afDlAlbums"));
    body.MusicMaxSongs = numberOrNull(view.querySelector("#afDlSongs"));
    body.MusicMaxGigabytes = numberOrNull(view.querySelector("#afDlMusicGb"), true);
    body.Users = users;
    saveHousehold(view, "Downloads", body, renderDownloads);
}

// Drawn from a working copy, so ticks and added accounts stay until Save.
function renderShared(view, accounts) {
    accounts = accounts || clone(STATE.household.SharedAccounts || []);
    var holder = view.querySelector("#afSharedList");
    holder.textContent = "";
    if (!accounts.length) {
        holder.appendChild(el("div", { className: "afEmpty", style: "padding:0.5em 0", text: "No shared accounts." }));
    }
    accounts.forEach(function (account) {
        var checks = peopleChecks(account.Members || [], account.UserId);
        var row = el("div", { className: "afDevice" }, [
            el("div", { className: "afDeviceHead" }, [
                el("span", { className: "afDeviceName", text: userName(account.UserId) }),
                el("span", { className: "afDeviceMeta", text: "Who watches on it:" }),
                el("span", { className: "afDeviceActions" }, [el("button", {
                    type: "button", className: "afSmallButton afDanger", text: "Remove", onclick: function () {
                        renderShared(view, collectShared(view).filter(function (a) { return !sameId(a.UserId, account.UserId); }));
                    }
                })])
            ]),
            checks
        ]);
        row.dataset.userId = account.UserId;
        holder.appendChild(row);
    });
    var picker = view.querySelector("#afSharedPick");
    picker.textContent = "";
    picker.appendChild(el("option", { value: "", text: "Choose an account…" }));
    STATE.users.forEach(function (user) {
        if (accounts.some(function (a) { return sameId(a.UserId, user.UserId); })) { return; }
        picker.appendChild(el("option", { value: user.UserId, text: user.Name }));
    });
    picker.value = "";
}

function collectShared(view) {
    var accounts = [];
    view.querySelectorAll("#afSharedList .afDevice").forEach(function (row) {
        accounts.push({ UserId: row.dataset.userId, Members: checkedIds(row) });
    });
    return accounts;
}

function saveShared(view) {
    var accounts = collectShared(view);
    if (accounts.some(function (a) { return !a.Members.length; })
        && !window.confirm("A shared account with nobody ticked isn't kept. Save anyway?")) { return; }
    saveHousehold(view, "SharedAccounts", accounts, function (v) { renderShared(v); });
}

function noticeState(starts, ends) {
    var now = Date.now();
    if (ends && new Date(ends).getTime() <= now) { return "Ended"; }
    if (starts && new Date(starts).getTime() > now) { return "Scheduled"; }
    return "Showing";
}

function audienceText(users) {
    if (!users || !users.length) { return "For everyone"; }
    return "For " + users.map(userName).join(", ");
}

function renderAnnouncements(view) {
    var holder = view.querySelector("#afAnnList");
    holder.textContent = "";
    view.querySelector("#afAnnEditor").hidden = true;
    var list = STATE.household.Announcements || [];
    if (!list.length) {
        holder.appendChild(el("div", { className: "afEmpty", style: "padding:0.5em 0", text: "No announcements." }));
    }
    list.forEach(function (a, index) {
        var times = [a.Starts ? "from " + when(a.Starts) : null, a.Ends ? "until " + when(a.Ends) : "no end"].filter(Boolean).join(" ");
        holder.appendChild(el("div", { className: "afDevice" }, [
            el("div", { className: "afDeviceHead" }, [
                el("span", { className: "afDeviceName", text: a.Title }),
                el("span", { className: "afBadge" + (a.Style === "warning" ? " afBadgeAccent" : ""), text: a.Style === "warning" ? "Warning" : "Info" }),
                el("span", { className: "afBadge", text: noticeState(a.Starts, a.Ends) }),
                el("span", { className: "afDeviceMeta", text: audienceText(a.Users) + " · " + times }),
                el("span", { className: "afDeviceActions" }, [
                    el("button", { type: "button", className: "afSmallButton", text: "Edit", onclick: function () { editAnnouncement(view, index); } }),
                    el("button", { type: "button", className: "afSmallButton afDanger", text: "Delete", onclick: function () {
                        if (!window.confirm("Delete the announcement \"" + a.Title + "\"?")) { return; }
                        saveHousehold(view, "Announcements", list.filter(function (_, i) { return i !== index; }), renderAnnouncements);
                    } })
                ])
            ]),
            a.Message ? el("div", { className: "afRowMeta", style: "white-space:pre-line", text: a.Message }) : null
        ]));
    });
}

function editAnnouncement(view, index) {
    var list = STATE.household.Announcements || [];
    var a = index == null ? null : list[index];
    var editor = view.querySelector("#afAnnEditor");
    editor.textContent = "";
    var title = el("input", { type: "text", className: "afControl", maxlength: "100" });
    title.value = a ? a.Title : "";
    var style = el("select", { className: "afControl" }, [el("option", { value: "info", text: "Info" }), el("option", { value: "warning", text: "Warning" })]);
    style.value = a ? a.Style : "info";
    var starts = el("input", { type: "datetime-local", className: "afControl" });
    starts.value = optionalLocalInput(a && a.Starts);
    var ends = el("input", { type: "datetime-local", className: "afControl" });
    ends.value = optionalLocalInput(a && a.Ends);
    var audience = el("select", { className: "afControl" }, [el("option", { value: "everyone", text: "Everyone" }), el("option", { value: "chosen", text: "Chosen people" })]);
    audience.value = a && a.Users && a.Users.length ? "chosen" : "everyone";
    var people = peopleChecks(a ? a.Users : []);
    people.hidden = audience.value !== "chosen";
    audience.onchange = function () { people.hidden = audience.value !== "chosen"; };
    var message = el("textarea", { className: "afControl", rows: "3", maxlength: "1000", style: "width:100%;margin-top:0.6em;resize:vertical", "aria-label": "Message" });
    message.value = a ? a.Message : "";
    editor.appendChild(el("h3", { className: "afSubTitle", text: a ? "Edit announcement" : "New announcement" }));
    editor.appendChild(el("div", { className: "afLimitGrid" }, [
        labeled("Title", title), labeled("Style", style),
        labeled("Starts (empty = now)", starts), labeled("Ends (empty = no end)", ends),
        labeled("Show to", audience)
    ]));
    editor.appendChild(people);
    editor.appendChild(message);
    editor.appendChild(el("div", { className: "afButtons" }, [
        el("button", { type: "button", className: "afSmallButton", text: "Save Announcement", onclick: function () {
            var item = a ? clone(a) : {};
            item.Title = title.value.trim();
            item.Message = message.value.trim();
            item.Style = style.value;
            // Unchanged times go back exactly as loaded.
            if (!a || starts.value !== optionalLocalInput(a.Starts)) { item.Starts = fromLocalInput(starts.value); }
            if (!a || ends.value !== optionalLocalInput(a.Ends)) { item.Ends = fromLocalInput(ends.value); }
            item.Users = audience.value === "chosen" ? checkedIds(people) : [];
            if (!item.Title) { Dashboard.alert({ title: "Arcfin Sync", message: "Give the announcement a title." }); return; }
            if (item.Starts && item.Ends && new Date(item.Ends) <= new Date(item.Starts)) {
                Dashboard.alert({ title: "Arcfin Sync", message: "The end has to be after the start." });
                return;
            }
            if (audience.value === "chosen" && !item.Users.length) {
                Dashboard.alert({ title: "Arcfin Sync", message: "Tick at least one person, or show it to everyone." });
                return;
            }
            var next = list.slice();
            if (a) { next[index] = item; } else { next.push(item); }
            saveHousehold(view, "Announcements", next, renderAnnouncements);
        } }),
        el("button", { type: "button", className: "afSmallButton", text: "Cancel", onclick: function () { editor.hidden = true; } })
    ]));
    editor.hidden = false;
    title.focus();
}

function renderMaintenance(view) {
    var holder = view.querySelector("#afMaintList");
    holder.textContent = "";
    view.querySelector("#afMaintEditor").hidden = true;
    var list = STATE.household.Maintenance || [];
    if (!list.length) {
        holder.appendChild(el("div", { className: "afEmpty", style: "padding:0.5em 0", text: "Nothing planned." }));
    }
    list.forEach(function (m, index) {
        var now = Date.now();
        var state = new Date(m.Ends).getTime() <= now ? "Ended" : new Date(m.Starts).getTime() > now ? "Planned" : "Under way";
        holder.appendChild(el("div", { className: "afDevice" }, [
            el("div", { className: "afDeviceHead" }, [
                el("span", { className: "afDeviceName", text: when(m.Starts) + " – " + when(m.Ends) }),
                el("span", { className: "afBadge" + (state === "Under way" ? " afBadgeAccent" : ""), text: state }),
                el("span", { className: "afDeviceMeta" }),
                el("span", { className: "afDeviceActions" }, [
                    el("button", { type: "button", className: "afSmallButton", text: "Edit", onclick: function () { editMaintenance(view, index); } }),
                    el("button", { type: "button", className: "afSmallButton afDanger", text: "Delete", onclick: function () {
                        if (!window.confirm("Delete this maintenance window?")) { return; }
                        saveHousehold(view, "Maintenance", list.filter(function (_, i) { return i !== index; }), renderMaintenance);
                    } })
                ])
            ]),
            m.Message ? el("div", { className: "afRowMeta", style: "white-space:pre-line", text: m.Message }) : null
        ]));
    });
}

function editMaintenance(view, index) {
    var list = STATE.household.Maintenance || [];
    var m = index == null ? null : list[index];
    var editor = view.querySelector("#afMaintEditor");
    editor.textContent = "";
    var starts = el("input", { type: "datetime-local", className: "afControl" });
    starts.value = toLocalInput(m ? m.Starts : null);
    var ends = el("input", { type: "datetime-local", className: "afControl" });
    ends.value = toLocalInput(m ? m.Ends : new Date(Date.now() + 3 * 3600000).toISOString());
    var message = el("textarea", { className: "afControl", rows: "3", maxlength: "500", placeholder: "What's happening, e.g. Moving to a new server", style: "width:100%;margin-top:0.6em;resize:vertical", "aria-label": "Message" });
    message.value = m ? m.Message : "";
    editor.appendChild(el("h3", { className: "afSubTitle", text: m ? "Edit maintenance" : "New maintenance" }));
    editor.appendChild(el("div", { className: "afLimitGrid" }, [labeled("Starts", starts), labeled("Ends", ends)]));
    editor.appendChild(message);
    editor.appendChild(el("div", { className: "afButtons" }, [
        el("button", { type: "button", className: "afSmallButton", text: "Save Maintenance", onclick: function () {
            var item = m ? clone(m) : {};
            if (!m || starts.value !== toLocalInput(m.Starts)) { item.Starts = fromLocalInput(starts.value); }
            if (!m || ends.value !== toLocalInput(m.Ends)) { item.Ends = fromLocalInput(ends.value); }
            item.Message = message.value.trim();
            if (!item.Starts || !item.Ends || new Date(item.Ends) <= new Date(item.Starts)) {
                Dashboard.alert({ title: "Arcfin Sync", message: "Pick a start, and an end after it." });
                return;
            }
            var next = list.slice();
            if (m) { next[index] = item; } else { next.push(item); }
            saveHousehold(view, "Maintenance", next, renderMaintenance);
        } }),
        el("button", { type: "button", className: "afSmallButton", text: "Cancel", onclick: function () { editor.hidden = true; } })
    ]));
    editor.hidden = false;
    starts.focus();
}

function renderHousehold(view) {
    renderDownloads(view);
    renderShared(view);
    renderAnnouncements(view);
    renderMaintenance(view);
}

// ----- Page -----

function renderAll(view) {
    renderOverview(view);
    renderSettings(view);
    renderUsers(view);
    renderPick(view);
    renderAccess(view, STATE.party, "#afPartyEnabled", "#afPartyUsers");
    renderRecs(view);
    renderHidden(view);
    renderNotifications(view);
    renderIntegrations(view);
    renderSecurity(view);
    renderLimits(view);
    renderHousehold(view);
    renderLive(view);
    renderHistorySettings(view);
    loadHistory(view);
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
        getJson("Schema"), getJson("Admin/Policy"), getJson("Admin/Users"), getJson("Status"), getJson("Admin/PickTogether"),
        getJson("Admin/WatchParty"), getJson("Admin/Recommendations"), getJson("Admin/Integrations"), getJson("Admin/Security"), getJson("Admin/Limits"),
        getJson("Admin/Live"), getJson("Admin/History/Settings"), getJson("Admin/Household"), getJson("Admin/Hidden"), getJson("Admin/Notifications")
    ]).then(function (results) {
        STATE.notifications = results[14];
        STATE.household = results[12];
        STATE.hidden = results[13];
        STATE.live = results[10];
        STATE.historySettings = results[11];
        STATE.pick = results[4];
        STATE.party = results[5];
        STATE.recs = results[6];
        STATE.integrations = results[7];
        STATE.security = results[8];
        STATE.limits = results[9];
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
    view.querySelector("#afPickSave").addEventListener("click", function () { savePick(view); });
    view.querySelector("#afPartySave").addEventListener("click", function () { saveParty(view); });
    view.querySelector("#afIntegrationsSave").addEventListener("click", function () { saveIntegrations(view, "seerr"); });
    view.querySelector("#afTmdbSave").addEventListener("click", function () { saveIntegrations(view, "tmdb"); });
    view.querySelector("#afSeerrRemove").addEventListener("click", function () {
        if (window.confirm("Remove the Seerr key? The apps go back to signing in to Seerr themselves.")) { saveIntegrations(view, "seerr", { SeerrApiKey: "" }); }
    });
    view.querySelector("#afTmdbRemove").addEventListener("click", function () {
        if (window.confirm("Remove the TMDB key?")) { saveIntegrations(view, "tmdb", { TmdbKey: "" }); }
    });
    view.querySelector("#afSeerrTest").addEventListener("click", function () {
        // What's typed now, saved or not; an empty key field tests the saved key.
        integrationAction(view, "POST", "Admin/Integrations/Test", function (d) { return SEERR_STATES[d.SeerrState] || "Add Seerr's local address and API key first."; },
            { SeerrUrl: view.querySelector("#afSeerrUrl").value || null, SeerrApiKey: view.querySelector("#afSeerrKey").value || null });
    });
    view.querySelector("#afQueueSend").addEventListener("click", function () { integrationAction(view, "POST", "Admin/Integrations/Queue/Send"); });
    view.querySelector("#afKeyGenerate").addEventListener("click", function () { makeKey(view, null); });
    view.querySelector("#afSecuritySave").addEventListener("click", function () { saveSecuritySettings(view); });
    view.querySelector("#afTrustOverHttp").addEventListener("change", function () {
        if (this.checked && !window.confirm("Trust the key over plain HTTP without checking? Devices stop asking for the fingerprint. On a network you don't trust, someone could pretend to be your server.")) { this.checked = false; return; }
        saveSecuritySettings(view);
    });
    view.querySelector("#afKeyReset").addEventListener("click", function () {
        if (!window.confirm("Replace the signing key completely? This can't be undone. The old key stops working, and every device has to trust the new one again; over plain HTTP that means checking its fingerprint on each device.")) { return; }
        if (window.prompt("Type REPLACE to confirm.") !== "REPLACE") { return; }
        Dashboard.showLoadingMsg();
        sendForJson("POST", "Admin/Security/Key/Reset").then(function (d) {
            STATE.security = d;
            renderSecurity(view);
            Dashboard.hideLoadingMsg();
        }, showError);
    });
    view.querySelector("#afKeyImport").addEventListener("click", function () {
        var pem = view.querySelector("#afKeyPem").value.trim();
        if (pem) { makeKey(view, pem); }
    });
    view.querySelector("#afLimitsSave").addEventListener("click", function () { saveLimits(view); });
    view.querySelector("#afSecurityEnabled").addEventListener("change", function () { saveSecuritySettings(view); });
    view.querySelector("#afTrustRequired").addEventListener("change", function () { saveSecuritySettings(view); });
    view.querySelector("#afHttpsAway").addEventListener("change", function () { saveSecuritySettings(view); });
    view.querySelector("#afTrustSave").addEventListener("click", function () { saveSecuritySettings(view); });
    view.querySelector("#afPinSave").addEventListener("click", function () { saveSecuritySettings(view); });
    view.querySelector("#afDlSave").addEventListener("click", function () { saveDownloads(view); });
    view.querySelector("#afSharedAdd").addEventListener("click", function () {
        var picker = view.querySelector("#afSharedPick");
        if (!picker.value) { return; }
        var accounts = collectShared(view);
        accounts.push({ UserId: picker.value, Members: [] });
        renderShared(view, accounts);
    });
    view.querySelector("#afSharedSave").addEventListener("click", function () { saveShared(view); });
    view.querySelector("#afAnnAdd").addEventListener("click", function () { editAnnouncement(view, null); });
    view.querySelector("#afMaintAdd").addEventListener("click", function () { editMaintenance(view, null); });
    view.querySelector("#afLiveRefresh").addEventListener("click", function () { loadLive(view); });
    view.querySelector("#afHistorySave").addEventListener("click", function () { saveHistorySettings(view); });
    view.querySelector("#afHistoryKeep").addEventListener("change", function () {
        view.querySelector("#afHistoryCustomWrap").hidden = view.querySelector("#afHistoryKeep").value !== "custom";
    });
    view.querySelector("#afHistoryKind").addEventListener("change", function () { loadHistory(view); });
    view.querySelector("#afHistoryClear").addEventListener("click", function () {
        if (!window.confirm("Clear all watch party and Pick Together history? This can't be undone.")) { return; }
        ApiClient.ajax({ type: "DELETE", url: api("Admin/History") }).then(function () { loadHistory(view); }, showError);
    });
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
