/*
 * File Hash - a QingToolbox mobile module.
 *
 * A web page cannot read an arbitrary file from the device, so the shell owns the picker
 * and streams the digest. The module asks, waits, and formats the answer.
 */
(function () {
    "use strict";

    var STRINGS = {
        "en": {
            title: "File Hash",
            subtitle: "Calculate MD5, SHA-1 and SHA-256 for one file.",
            pick: "Choose file",
            fileLabel: "Selected file",
            empty: "No file selected yet.",
            name: "Name",
            size: "Size",
            copy: "Copy all hashes",
            copied: "Hashes copied",
            cancelled: "No file was chosen.",
            note: "Hashing streams the file on this device. Nothing is uploaded.",
            error: "The file could not be hashed."
        },
        "zh": {
            title: "文件哈希",
            subtitle: "计算单个文件的 MD5、SHA-1 与 SHA-256。",
            pick: "选择文件",
            fileLabel: "已选文件",
            empty: "尚未选择文件。",
            name: "名称",
            size: "大小",
            copy: "复制全部哈希",
            copied: "哈希已复制",
            cancelled: "未选择任何文件。",
            note: "在本机流式计算，不会上传任何内容。",
            error: "无法计算此文件的哈希。"
        }
    };

    var locale = String(qing.host().locale || "en").toLowerCase();
    var isChinese = locale.indexOf("zh") === 0;
    var t = isChinese ? STRINGS.zh : STRINGS.en;

    var ALGORITHMS = ["md5", "sha1", "sha256"];

    var error = document.getElementById("error");
    var details = document.getElementById("file-details");
    var hashesRoot = document.getElementById("hashes");
    var pickButton = document.getElementById("pick");
    var summaryLines = [];

    function showError(message) {
        if (!message) {
            error.textContent = "";
            error.classList.add("qing-hidden");
            return;
        }
        error.textContent = message;
        error.classList.remove("qing-hidden");
    }

    function formatSize(value) {
        var bytes = Number(value);
        if (!isFinite(bytes) || bytes < 0) {
            return "—";
        }
        var units = ["B", "KB", "MB", "GB"];
        var index = 0;
        var size = bytes;
        while (size >= 1024 && index < units.length - 1) {
            size /= 1024;
            index += 1;
        }
        var rounded = index === 0 ? size.toFixed(0) : size.toFixed(2);
        return rounded + " " + units[index];
    }

    function renderHashes(answer) {
        hashesRoot.innerHTML = "";
        summaryLines = [];
        ALGORITHMS.forEach(function (name) {
            if (!answer.hashes || !answer.hashes[name]) {
                return;
            }
            var label = document.createElement("p");
            label.className = "qing-label";
            label.style.marginTop = "10px";
            label.textContent = name.toUpperCase();

            var value = document.createElement("p");
            value.className = "qing-mono";
            value.textContent = answer.hashes[name];

            hashesRoot.appendChild(label);
            hashesRoot.appendChild(value);
            summaryLines.push(name.toUpperCase() + ": " + answer.hashes[name]);
        });
    }

    function showAnswer(answer) {
        document.getElementById("name-value").textContent = answer.name || "—";
        document.getElementById("size-value").textContent = formatSize(answer.size);
        renderHashes(answer);
        details.classList.remove("qing-hidden");
        document.getElementById("empty").classList.add("qing-hidden");
    }

    function render() {
        document.getElementById("title").textContent = t.title;
        document.getElementById("subtitle").textContent = t.subtitle;
        document.getElementById("file-label").textContent = t.fileLabel;
        document.getElementById("empty").textContent = t.empty;
        document.getElementById("name-label").textContent = t.name;
        document.getElementById("size-label").textContent = t.size;
        document.getElementById("copy").textContent = t.copy;
        document.getElementById("note").textContent = t.note;
        pickButton.textContent = t.pick;
        document.documentElement.lang = isChinese ? "zh-CN" : "en";
    }

    function pick() {
        showError("");
        pickButton.disabled = true;
        qing.invoke("file.hash", { algorithms: ALGORITHMS })
            .then(showAnswer)
            .catch(function (failure) {
                if (failure && failure.message === "file-pick-cancelled") {
                    showError(t.cancelled);
                } else {
                    showError(t.error);
                }
            })
            .then(function () {
                pickButton.disabled = false;
            });
    }

    pickButton.addEventListener("click", pick);
    document.getElementById("copy").addEventListener("click", function () {
        if (summaryLines.length === 0) {
            return;
        }
        qing.copy(summaryLines.join("\n"));
        qing.toast(t.copied);
    });

    render();
})();
