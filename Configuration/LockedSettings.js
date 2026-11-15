var ArcfinSyncPluginId = "f3a1c9d2-6b4e-4a7f-9c3d-1e2f3a4b5c6d";

var LANGUAGE_OPTIONS = [
    ["", "None"], ["eng", "English"], ["spa", "Spanish"], ["fre", "French"], ["ger", "German"],
    ["jpn", "Japanese"], ["kor", "Korean"], ["chi", "Chinese"], ["ita", "Italian"],
    ["por", "Portuguese"], ["rus", "Russian"]
];

// Mirrors the sections and settings in the Arcfin app's own Settings screens. Keeping this in
// sync with SettingsSyncRegistry (SyncEngine.swift) and the app's own @AppStorage keys is a
// manual step — there's no shared source of truth across the C#/Swift boundary.
var ARCFIN_SETTINGS_SCHEMA = [
    { section: "Playback", key: "videoQuality", label: "Video Quality", type: "select", default: "Max",
        options: [["Max", "Max Quality"], ["auto", "Auto (Recommended)"], ["4K", "4K Ultra HD"], ["1080p", "1080p HD"], ["720p", "720p"], ["480p", "480p"]] },
    { section: "Playback", key: "playbackEngine", label: "Playback Engine", type: "select", default: "Auto",
        options: [["Auto", "Auto"], ["AVFoundation", "AVFoundation"], ["VLC", "VLC"]] },
    { section: "Playback", key: "autoPlayNext", label: "Auto-play Next Episode", type: "bool", default: "true" },
    { section: "Playback", key: "skipIntro", label: "Skip Intro Automatically", type: "bool", default: "false" },
    { section: "Playback", key: "skipCredits", label: "Skip Credits Automatically", type: "bool", default: "false" },
    { section: "Playback", key: "skipRecap", label: "Skip Recap Automatically", type: "bool", default: "false" },
    { section: "Playback", key: "subtitleDefault", label: "Show Subtitles by Default", type: "bool", default: "false" },
    { section: "Playback", key: "trailersEnabled", label: "Trailers", type: "bool", default: "true" },
    { section: "Playback", key: "youtubeTrailersEnabled", label: "YouTube Fallback", type: "bool", default: "true" },
    { section: "Playback", key: "preferredAudioLanguage", label: "Preferred Audio Language", type: "select", default: "", options: LANGUAGE_OPTIONS },
    { section: "Playback", key: "preferredSubtitleLanguage", label: "Preferred Subtitle Language", type: "select", default: "", options: LANGUAGE_OPTIONS },
    { section: "Playback", key: "skipForwardSeconds", label: "Skip Forward (seconds)", type: "number", default: "10" },
    { section: "Playback", key: "skipBackwardSeconds", label: "Skip Backward (seconds)", type: "number", default: "10" },

    { section: "Downloads", key: "wifiOnly", label: "Wi-Fi Only", type: "bool", default: "true" },
    { section: "Downloads", key: "downloadQuality", label: "Download Quality", type: "select", default: "Max",
        options: [["Max", "Original"], ["high", "High"], ["balanced", "Balanced"], ["spaceSaver", "Space Saver"]] },
    { section: "Downloads", key: "offlineModeEnabled", label: "Offline Mode", type: "bool", default: "false" },
    { section: "Downloads", key: "smartDownloadsEnabled", label: "Smart Downloads", type: "bool", default: "false" },
    { section: "Downloads", key: "smartDownloadsEpisodeCount", label: "Episodes to Keep", type: "number", default: "2" },
    { section: "Downloads", key: "smartDownloadsStorageBudgetGB", label: "Storage Budget (GB)", type: "number", default: "10" },

    { section: "Appearance", key: "reduceMotion", label: "Reduce Motion", type: "bool", default: "false" },
    { section: "Appearance", key: "hapticsEnabled", label: "Haptic Feedback", type: "bool", default: "true" },
    { section: "Appearance", key: "mergeContinueWatchingAndUpNext", label: "Merge Continue Watching & Up Next", type: "bool", default: "false" },
    { section: "Appearance", key: "heroVideoBackdropsEnabled", label: "Hero Video Backdrops", type: "bool", default: "true" },

    { section: "Notifications", key: "notifyNewContent", label: "New Content Added", type: "bool", default: "true" },
    { section: "Notifications", key: "notifyDownload", label: "Download Complete", type: "bool", default: "true" },

    { section: "Player Features", key: "chapterListEnabled", label: "Chapter List", type: "bool", default: "true" },
    { section: "Player Features", key: "syncPlayEnabled", label: "SyncPlay", type: "bool", default: "true" },
    { section: "Player Features", key: "castEnabled", label: "Chromecast", type: "bool", default: "true" },
    { section: "Player Features", key: "stillWatchingPromptEnabled", label: "\"Are You Still Watching?\" Prompt", type: "bool", default: "true" },
    { section: "Player Features", key: "matchContentFrameRate", label: "Match Content Frame Rate", type: "bool", default: "false" },

    { section: "Subtitle Style", key: "subtitleTextSize", label: "Size", type: "select", default: "medium",
        options: [["small", "Small"], ["medium", "Medium"], ["large", "Large"], ["extraLarge", "Extra Large"]] },
    { section: "Subtitle Style", key: "subtitleTextColor", label: "Colour", type: "select", default: "white",
        options: [["white", "White"], ["yellow", "Yellow"], ["cyan", "Cyan"], ["green", "Green"]] },
    { section: "Subtitle Style", key: "subtitleBackgroundStyle", label: "Background", type: "select", default: "outline",
        options: [["box", "Box"], ["outline", "Outline"], ["none", "None"]] },

    { section: "Integrations", key: "seerrDiscoverEnabled", label: "Show Discover on Home", type: "bool", default: "true" },
    { section: "Integrations", key: "seerrURL", label: "Seerr Server URL", type: "text", default: "" },
    { section: "Integrations", key: "seerrAuthMethod", label: "Seerr Account Type", type: "select", default: "jellyfin",
        options: [["jellyfin", "Jellyfin Account"], ["local", "Local Account"]] },
    { section: "Integrations", type: "note", text: "There's no Seerr Username/Password row here on purpose. Lock Account Type to Jellyfin Account and every user's Seerr username is automatically their Jellyfin username, no admin input needed. Lock it to Local Account (or leave it unlocked) and each user enters their own separate Seerr username/password in the app instead. A password can never be set here either way — Arcfin only ever stores a user's Jellyfin access token, never their raw password." },
    { section: "Integrations", key: "streamystatsURL", label: "Streamystats Server URL", type: "text", default: "" }
];

function arcfinSyncTabs() {
    return [
        { href: "configurationpage?name=" + encodeURIComponent("General"), name: "General" },
        { href: "configurationpage?name=" + encodeURIComponent("Client Settings"), name: "Client Settings" },
        { href: "configurationpage?name=" + encodeURIComponent("Advanced"), name: "Advanced" }
    ];
}

function renderSections(container, existingByKey) {
    container.innerHTML = "";
    var currentSection = null;
    var sectionDiv = null;

    ARCFIN_SETTINGS_SCHEMA.forEach(function (item) {
        if (item.section !== currentSection) {
            currentSection = item.section;
            var heading = document.createElement("h3");
            heading.className = "arcfinSectionHeading";
            heading.textContent = currentSection;
            container.appendChild(heading);
            sectionDiv = document.createElement("div");
            container.appendChild(sectionDiv);
        }

        if (item.type === "note") {
            var note = document.createElement("p");
            note.className = "fieldDescription";
            note.textContent = item.text;
            sectionDiv.appendChild(note);
            return;
        }

        var existing = existingByKey[item.key];
        var storedValue = existing ? existing.PrefillValue : null;
        var displayValue = (storedValue === null || storedValue === undefined || storedValue === "") ? item.default : storedValue;

        var row = document.createElement("div");
        row.className = "arcfinSettingRow";
        row.dataset.key = item.key;
        row.dataset.type = item.type;

        var labelDiv = document.createElement("div");
        labelDiv.className = "arcfinSettingLabel";
        var labelText = document.createElement("div");
        labelText.textContent = item.label;
        var keyText = document.createElement("span");
        keyText.className = "arcfinSettingKey";
        keyText.textContent = item.key;
        labelDiv.appendChild(labelText);
        labelDiv.appendChild(keyText);

        var controlDiv = document.createElement("div");
        controlDiv.className = "arcfinSettingControl";
        var control;
        if (item.type === "bool") {
            control = document.createElement("input");
            control.type = "checkbox";
            control.checked = displayValue === "true";
        } else if (item.type === "select") {
            control = document.createElement("select");
            control.setAttribute("is", "emby-select");
            item.options.forEach(function (opt) {
                var o = document.createElement("option");
                o.value = opt[0];
                o.textContent = opt[1];
                control.appendChild(o);
            });
            control.value = displayValue;
        } else if (item.type === "number") {
            control = document.createElement("input");
            control.type = "number";
            control.value = displayValue;
        } else {
            control = document.createElement("input");
            control.type = "text";
            control.value = displayValue;
        }
        control.className = "arcfinSettingValueControl";
        controlDiv.appendChild(control);

        var lockLabel = document.createElement("label");
        lockLabel.className = "arcfinSettingLock";
        var lockCheckbox = document.createElement("input");
        lockCheckbox.type = "checkbox";
        lockCheckbox.className = "arcfinSettingLockControl";
        lockCheckbox.checked = !!(existing && existing.IsLocked);
        var lockText = document.createElement("span");
        lockText.textContent = "Lock";
        lockLabel.appendChild(lockCheckbox);
        lockLabel.appendChild(lockText);

        row.appendChild(labelDiv);
        row.appendChild(controlDiv);
        row.appendChild(lockLabel);
        sectionDiv.appendChild(row);
    });
}

function collectSettings(container) {
    var rows = container.querySelectorAll(".arcfinSettingRow");
    var result = [];
    rows.forEach(function (row) {
        var valueControl = row.querySelector(".arcfinSettingValueControl");
        var lockControl = row.querySelector(".arcfinSettingLockControl");
        var value = row.dataset.type === "bool" ? (valueControl.checked ? "true" : "false") : valueControl.value;
        result.push({ Key: row.dataset.key, IsLocked: lockControl.checked, PrefillValue: value });
    });
    return result;
}

export default function (view) {
    view.addEventListener("viewshow", function () {
        LibraryMenu.setTabs("Client Settings", 1, arcfinSyncTabs);
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(ArcfinSyncPluginId).then(function (config) {
            var existingByKey = {};
            (config.LockedSettings || []).forEach(function (s) { existingByKey[s.Key] = s; });
            renderSections(document.querySelector("#ArcfinClientSettingsSections"), existingByKey);
            Dashboard.hideLoadingMsg();
        });

        var form = document.querySelector("#ArcfinSyncLockedForm");
        if (form.dataset.arcfinSyncWired) { return; }
        form.dataset.arcfinSyncWired = "true";
        form.addEventListener("submit", function (e) {
            e.preventDefault();
            Dashboard.showLoadingMsg();
            ApiClient.getPluginConfiguration(ArcfinSyncPluginId).then(function (config) {
                config.LockedSettings = collectSettings(document.querySelector("#ArcfinClientSettingsSections"));
                ApiClient.updatePluginConfiguration(ArcfinSyncPluginId, config).then(function (result) {
                    Dashboard.processPluginConfigurationUpdateResult(result);
                });
            });
            return false;
        });
    });
}
