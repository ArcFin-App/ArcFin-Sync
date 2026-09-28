var ArcfinSyncPluginId = "f3a1c9d2-6b4e-4a7f-9c3d-1e2f3a4b5c6d";

function arcfinSyncTabs() {
    return [
        { href: "configurationpage?name=" + encodeURIComponent("General"), name: "General" },
        { href: "configurationpage?name=" + encodeURIComponent("Locked Settings"), name: "Locked Settings" },
        { href: "configurationpage?name=" + encodeURIComponent("Advanced"), name: "Advanced" }
    ];
}

function addLockedSettingRow(setting) {
    setting = setting || { Key: "", IsLocked: false, PrefillValue: "" };
    var row = document.createElement("tr");

    var keyCell = document.createElement("td");
    var keyInput = document.createElement("input");
    keyInput.type = "text";
    keyInput.className = "arcfinLockKey";
    keyInput.setAttribute("list", "ArcfinKnownSettingKeys");
    keyInput.value = setting.Key || "";
    keyCell.appendChild(keyInput);

    var lockedCell = document.createElement("td");
    var lockedInput = document.createElement("input");
    lockedInput.type = "checkbox";
    lockedInput.className = "arcfinLockIsLocked";
    lockedInput.checked = !!setting.IsLocked;
    lockedCell.appendChild(lockedInput);

    var valueCell = document.createElement("td");
    var valueInput = document.createElement("input");
    valueInput.type = "text";
    valueInput.className = "arcfinLockPrefill";
    valueInput.value = setting.PrefillValue || "";
    valueCell.appendChild(valueInput);

    var removeCell = document.createElement("td");
    var removeButton = document.createElement("button");
    removeButton.type = "button";
    removeButton.textContent = "Remove";
    removeButton.addEventListener("click", function () { row.remove(); });
    removeCell.appendChild(removeButton);

    row.appendChild(keyCell);
    row.appendChild(lockedCell);
    row.appendChild(valueCell);
    row.appendChild(removeCell);
    document.querySelector("#LockedSettingsRows").appendChild(row);
}

function collectLockedSettings() {
    var rows = document.querySelectorAll("#LockedSettingsRows tr");
    var result = [];
    rows.forEach(function (row) {
        var key = row.querySelector(".arcfinLockKey").value.trim();
        if (!key) { return; }
        result.push({
            Key: key,
            IsLocked: row.querySelector(".arcfinLockIsLocked").checked,
            PrefillValue: row.querySelector(".arcfinLockPrefill").value
        });
    });
    return result;
}

export default function (view) {
    view.addEventListener("viewshow", function () {
        LibraryMenu.setTabs("Locked Settings", 1, arcfinSyncTabs);

        var addButton = document.querySelector("#AddLockedSettingRow");
        if (!addButton.dataset.arcfinSyncWired) {
            addButton.dataset.arcfinSyncWired = "true";
            addButton.addEventListener("click", function () { addLockedSettingRow(); });
        }

        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(ArcfinSyncPluginId).then(function (config) {
            document.querySelector("#LockedSettingsRows").innerHTML = "";
            (config.LockedSettings || []).forEach(addLockedSettingRow);
            Dashboard.hideLoadingMsg();
        });

        var form = document.querySelector("#ArcfinSyncLockedForm");
        if (form.dataset.arcfinSyncWired) { return; }
        form.dataset.arcfinSyncWired = "true";
        form.addEventListener("submit", function (e) {
            e.preventDefault();
            Dashboard.showLoadingMsg();
            ApiClient.getPluginConfiguration(ArcfinSyncPluginId).then(function (config) {
                config.LockedSettings = collectLockedSettings();
                ApiClient.updatePluginConfiguration(ArcfinSyncPluginId, config).then(function (result) {
                    Dashboard.processPluginConfigurationUpdateResult(result);
                });
            });
            return false;
        });
    });
}
