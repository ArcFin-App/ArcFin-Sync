var ArcfinSyncPluginId = "f3a1c9d2-6b4e-4a7f-9c3d-1e2f3a4b5c6d";

function arcfinSyncTabs() {
    return [
        { href: "configurationpage?name=" + encodeURIComponent("General"), name: "General" },
        { href: "configurationpage?name=" + encodeURIComponent("Client Settings"), name: "Client Settings" },
        { href: "configurationpage?name=" + encodeURIComponent("Advanced"), name: "Advanced" }
    ];
}

export default function (view) {
    view.addEventListener("viewshow", function () {
        LibraryMenu.setTabs("Advanced", 2, arcfinSyncTabs);
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(ArcfinSyncPluginId).then(function (config) {
            document.querySelector("#AdvancedYamlConfig").value = config.AdvancedYamlConfig || "";
            Dashboard.hideLoadingMsg();
        });

        var form = document.querySelector("#ArcfinSyncAdvancedForm");
        if (form.dataset.arcfinSyncWired) { return; }
        form.dataset.arcfinSyncWired = "true";
        form.addEventListener("submit", function (e) {
            e.preventDefault();
            Dashboard.showLoadingMsg();
            ApiClient.getPluginConfiguration(ArcfinSyncPluginId).then(function (config) {
                config.AdvancedYamlConfig = document.querySelector("#AdvancedYamlConfig").value;
                ApiClient.updatePluginConfiguration(ArcfinSyncPluginId, config).then(function (result) {
                    Dashboard.processPluginConfigurationUpdateResult(result);
                });
            });
            return false;
        });
    });
}
