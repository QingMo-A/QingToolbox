/*
 * Text Codec - a QingToolbox mobile module.
 *
 * The conversions themselves are done by the shell: a web page cannot be trusted to
 * implement Base64 over UTF-8 correctly, and the shell already has a tested codec. The
 * module owns the interface and the wording, the shell owns the algorithm.
 */
(function () {
    "use strict";

    var STRINGS = {
        "en": {
            title: "Text Codec",
            subtitle: "Convert Base64 and URL text on this device.",
            inputLabel: "Input",
            inputPlaceholder: "Type or paste the text to convert",
            resultLabel: "Result",
            resultEmpty: "The converted text appears here.",
            note: "Conversions run in the shell and never leave this device.",
            convert: "Convert",
            clear: "Clear",
            copy: "Copy result",
            copied: "Result copied",
            operations: {
                "base64-encode": "Base64 encode",
                "base64-decode": "Base64 decode",
                "url-encode": "URL encode",
                "url-decode": "URL decode"
            },
            errors: {
                "INVALID_BASE64": "That is not valid Base64.",
                "INVALID_URL_PERCENT": "That URL encoding is incomplete.",
                "INVALID_URL_UTF8": "That URL encoding is not valid UTF-8.",
                "DEFAULT": "The text could not be converted."
            }
        },
        "zh": {
            title: "文本编解码",
            subtitle: "在本机转换 Base64 与 URL 文本。",
            inputLabel: "输入",
            inputPlaceholder: "输入或粘贴要转换的文本",
            resultLabel: "结果",
            resultEmpty: "转换后的文本会显示在这里。",
            note: "转换在外壳中完成，不会离开本机。",
            convert: "转换",
            clear: "清空",
            copy: "复制结果",
            copied: "结果已复制",
            operations: {
                "base64-encode": "Base64 编码",
                "base64-decode": "Base64 解码",
                "url-encode": "URL 编码",
                "url-decode": "URL 解码"
            },
            errors: {
                "INVALID_BASE64": "不是有效的 Base64。",
                "INVALID_URL_PERCENT": "URL 编码不完整。",
                "INVALID_URL_UTF8": "URL 编码不是有效的 UTF-8。",
                "DEFAULT": "无法转换此文本。"
            }
        }
    };

    var ORDER = ["base64-encode", "base64-decode", "url-encode", "url-decode"];

    var locale = String(qing.host().locale || "en").toLowerCase();
    var t = locale.indexOf("zh") === 0 ? STRINGS.zh : STRINGS.en;

    var input = document.getElementById("input");
    var result = document.getElementById("result");
    var error = document.getElementById("error");
    var operations = document.getElementById("operations");

    var operation = ORDER[0];
    var buttons = {};

    function showError(message) {
        if (!message) {
            error.classList.add("qing-hidden");
            error.textContent = "";
            return;
        }
        error.textContent = message;
        error.classList.remove("qing-hidden");
    }

    function render() {
        document.getElementById("title").textContent = t.title;
        document.getElementById("subtitle").textContent = t.subtitle;
        document.getElementById("input-label").textContent = t.inputLabel;
        document.getElementById("result-label").textContent = t.resultLabel;
        document.getElementById("note").textContent = t.note;
        document.getElementById("convert").textContent = t.convert;
        document.getElementById("clear").textContent = t.clear;
        document.getElementById("copy").textContent = t.copy;
        input.placeholder = t.inputPlaceholder;
        result.textContent = t.resultEmpty;
        document.documentElement.lang = locale.indexOf("zh") === 0 ? "zh-CN" : "en";

        ORDER.forEach(function (name) {
            var button = document.createElement("button");
            button.type = "button";
            button.textContent = t.operations[name];
            button.setAttribute("aria-pressed", String(name === operation));
            button.addEventListener("click", function () {
                operation = name;
                Object.keys(buttons).forEach(function (key) {
                    buttons[key].setAttribute("aria-pressed", String(key === operation));
                });
                if (input.value.length > 0) {
                    convert();
                }
            });
            buttons[name] = button;
            operations.appendChild(button);
        });
    }

    function convert() {
        showError("");
        if (input.value.length === 0) {
            result.textContent = t.resultEmpty;
            return;
        }
        try {
            var answer = qing.call("text.codec", { operation: operation, input: input.value });
            result.textContent = answer.result.length > 0 ? answer.result : t.resultEmpty;
        } catch (failure) {
            result.textContent = t.resultEmpty;
            showError(t.errors[failure.message] || t.errors.DEFAULT);
        }
    }

    function clear() {
        input.value = "";
        result.textContent = t.resultEmpty;
        showError("");
    }

    function copyResult() {
        if (result.textContent === t.resultEmpty) {
            return;
        }
        qing.copy(result.textContent);
        qing.toast(t.copied);
    }

    document.getElementById("convert").addEventListener("click", convert);
    document.getElementById("clear").addEventListener("click", clear);
    document.getElementById("copy").addEventListener("click", copyResult);
    input.addEventListener("input", function () {
        if (input.value.length === 0) {
            result.textContent = t.resultEmpty;
            showError("");
        }
    });

    render();
})();
