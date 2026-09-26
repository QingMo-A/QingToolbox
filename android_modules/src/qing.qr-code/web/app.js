/*
 * QR Code - a QingToolbox mobile module.
 *
 * Encoding uses the shell's barcode encoder and sharing uses the system share sheet, so the
 * module only owns the interface. The image never leaves the app until the user shares it.
 */
(function () {
    "use strict";

    var STRINGS = {
        "en": {
            title: "QR Code",
            subtitle: "Generate a QR code from any text.",
            inputLabel: "Text",
            inputPlaceholder: "Text, a link, or several lines",
            previewLabel: "Preview",
            empty: "The QR code appears here.",
            note: "Images are only written to the app cache when you share one.",
            generate: "Generate",
            clear: "Clear",
            share: "Share image",
            copy: "Copy text",
            copied: "Text copied",
            shareFailed: "The image could not be shared.",
            emptyInput: "Enter some text first.",
            error: "A QR code could not be generated for that text."
        },
        "zh": {
            title: "二维码",
            subtitle: "把任意文本生成二维码。",
            inputLabel: "文本",
            inputPlaceholder: "文本、链接或多行内容",
            previewLabel: "预览",
            empty: "二维码会显示在这里。",
            note: "仅在选择分享时才把图片写入应用缓存。",
            generate: "生成",
            clear: "清空",
            share: "分享图片",
            copy: "复制文本",
            copied: "文本已复制",
            shareFailed: "无法分享图片。",
            emptyInput: "请先输入文本。",
            error: "无法为此文本生成二维码。"
        }
    };

    var locale = String(qing.host().locale || "en").toLowerCase();
    var isChinese = locale.indexOf("zh") === 0;
    var t = isChinese ? STRINGS.zh : STRINGS.en;

    var input = document.getElementById("input");
    var image = document.getElementById("image");
    var empty = document.getElementById("empty");
    var actions = document.getElementById("actions");
    var error = document.getElementById("error");
    var currentText = "";

    function showError(message) {
        if (!message) {
            error.textContent = "";
            error.classList.add("qing-hidden");
            return;
        }
        error.textContent = message;
        error.classList.remove("qing-hidden");
    }

    function showImage(dataUrl) {
        image.src = dataUrl;
        image.classList.remove("qing-hidden");
        empty.classList.add("qing-hidden");
        actions.classList.remove("qing-hidden");
    }

    function resetPreview() {
        image.removeAttribute("src");
        image.classList.add("qing-hidden");
        actions.classList.add("qing-hidden");
        empty.classList.remove("qing-hidden");
        currentText = "";
    }

    function render() {
        document.getElementById("title").textContent = t.title;
        document.getElementById("subtitle").textContent = t.subtitle;
        document.getElementById("input-label").textContent = t.inputLabel;
        document.getElementById("preview-label").textContent = t.previewLabel;
        document.getElementById("note").textContent = t.note;
        document.getElementById("generate").textContent = t.generate;
        document.getElementById("clear").textContent = t.clear;
        document.getElementById("share").textContent = t.share;
        document.getElementById("copy").textContent = t.copy;
        empty.textContent = t.empty;
        input.placeholder = t.inputPlaceholder;
        document.documentElement.lang = isChinese ? "zh-CN" : "en";
    }

    function generate() {
        showError("");
        var text = input.value;
        if (text.length === 0) {
            showError(t.emptyInput);
            resetPreview();
            return;
        }
        try {
            var answer = qing.call("graphics.qr", { text: text });
            currentText = text;
            showImage(answer.png);
        } catch (failure) {
            resetPreview();
            showError(t.error);
        }
    }

    function clear() {
        input.value = "";
        resetPreview();
        showError("");
    }

    function share() {
        if (!currentText || !image.src) {
            return;
        }
        showError("");
        qing.invoke("graphics.share", { dataUrl: image.src, name: "qing-qr-code" }).catch(function () {
            showError(t.shareFailed);
        });
    }

    document.getElementById("generate").addEventListener("click", generate);
    document.getElementById("clear").addEventListener("click", clear);
    document.getElementById("share").addEventListener("click", share);
    document.getElementById("copy").addEventListener("click", function () {
        if (!currentText) {
            return;
        }
        qing.copy(currentText);
        qing.toast(t.copied);
    });

    render();
    resetPreview();
})();
