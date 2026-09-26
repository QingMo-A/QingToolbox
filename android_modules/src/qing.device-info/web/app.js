/*
 * Device Info - a QingToolbox mobile module.
 *
 * The shell reads the properties (it alone can) and the module decides how they read. The
 * snapshot arrives as stable keys, never as pre-formatted sentences, so the wording stays
 * inside the module where it can be translated.
 */
(function () {
    "use strict";

    var STRINGS = {
        "en": {
            title: "Device Info",
            subtitle: "Public properties only, read on this device.",
            copy: "Copy summary",
            copied: "Summary copied",
            note: "Read when this page opened. Nothing is stored or uploaded.",
            sections: {
                device: "Device",
                android: "Android",
                hardware: "Hardware",
                display: "Display",
                app: "Shell"
            },
            labels: {
                manufacturer: "Manufacturer",
                brand: "Brand",
                model: "Model",
                codename: "Device",
                product: "Product",
                release: "Android version",
                apiLevel: "API level",
                buildId: "Build ID",
                securityPatch: "Security patch",
                abis: "Supported ABIs",
                processors: "Processors",
                size: "Screen size",
                density: "Density",
                densityDpi: "Density DPI",
                shellVersion: "Version",
                shellVersionCode: "Version code",
                shellPackage: "Package"
            },
            error: "The shell could not read this device."
        },
        "zh": {
            title: "设备信息",
            subtitle: "仅读取公开属性，全部在本机完成。",
            copy: "复制摘要",
            copied: "摘要已复制",
            note: "打开本页时读取一次，不会保存或上传。",
            sections: {
                device: "设备",
                android: "Android",
                hardware: "硬件",
                display: "显示",
                app: "外壳"
            },
            labels: {
                manufacturer: "制造商",
                brand: "品牌",
                model: "型号",
                codename: "设备代号",
                product: "产品",
                release: "Android 版本",
                apiLevel: "API 级别",
                buildId: "Build ID",
                securityPatch: "安全补丁",
                abis: "支持的 ABI",
                processors: "处理器数",
                size: "屏幕尺寸",
                density: "密度",
                densityDpi: "Density DPI",
                shellVersion: "版本",
                shellVersionCode: "版本号",
                shellPackage: "包名"
            },
            error: "外壳无法读取本设备信息。"
        }
    };

    var locale = String(qing.host().locale || "en").toLowerCase();
    var isChinese = locale.indexOf("zh") === 0;
    var t = isChinese ? STRINGS.zh : STRINGS.en;

    var sectionsRoot = document.getElementById("sections");
    var summaryLines = [];

    function label(key) {
        return t.labels[key] || key;
    }

    function renderSection(section) {
        var card = document.createElement("section");
        card.className = "qing-card";

        var title = document.createElement("p");
        title.className = "qing-label";
        title.textContent = t.sections[section.title] || section.title;
        card.appendChild(title);

        var rows = document.createElement("div");
        rows.className = "qing-rows";
        section.items.forEach(function (item) {
            var row = document.createElement("div");
            row.className = "qing-row";

            var name = document.createElement("p");
            name.className = "qing-row-label";
            name.textContent = label(item.label);

            var value = document.createElement("p");
            value.className = "qing-row-value";
            value.textContent = item.value;

            row.appendChild(name);
            row.appendChild(value);
            rows.appendChild(row);

            summaryLines.push(label(item.label) + ": " + item.value);
        });

        card.appendChild(rows);
        sectionsRoot.appendChild(card);
    }

    function boot() {
        document.getElementById("title").textContent = t.title;
        document.getElementById("subtitle").textContent = t.subtitle;
        document.getElementById("copy").textContent = t.copy;
        document.getElementById("note").textContent = t.note;
        document.documentElement.lang = isChinese ? "zh-CN" : "en";

        try {
            var snapshot = qing.call("device.snapshot", {});
            snapshot.sections.forEach(renderSection);
        } catch (failure) {
            sectionsRoot.innerHTML = "";
            var message = document.createElement("p");
            message.className = "qing-card qing-error";
            message.textContent = t.error;
            sectionsRoot.appendChild(message);
        }
    }

    document.getElementById("copy").addEventListener("click", function () {
        if (summaryLines.length === 0) {
            return;
        }
        qing.copy(summaryLines.join("\n"));
        qing.toast(t.copied);
    });

    boot();
})();
