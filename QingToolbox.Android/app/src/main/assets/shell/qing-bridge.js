/*
 * QingToolbox mobile module bridge.
 *
 * Injected into every module page by the shell before the module's own scripts run. It is
 * the only sanctioned way out of the page: everything routes through the shell's bridge
 * object, which checks the capabilities the module declared in its manifest.
 *
 *   qing.host()                              -> { apiVersion, moduleId, locale, ... }
 *   qing.call(method, params)                -> result (immediate)
 *   qing.invoke(method, params)              -> Promise (answers once the user replied)
 *   qing.can(capability)                     -> boolean
 *   qing.copy(text) / qing.toast(message)    -> convenience wrappers
 */
(function () {
    "use strict";

    var pending = Object.create(null);
    var counter = 0;
    var info = null;

    function bridge() {
        if (!window.qingHost) {
            throw new Error("qing-host-unavailable");
        }
        return window.qingHost;
    }

    function unwrap(raw) {
        var payload;
        try {
            payload = JSON.parse(raw);
        } catch (error) {
            throw new Error("qing-host-protocol");
        }
        if (payload && payload.error) {
            throw new Error(payload.error);
        }
        return payload ? payload.data : undefined;
    }

    function call(method, params) {
        return unwrap(bridge().call(String(method), JSON.stringify(params || {})));
    }

    function invoke(method, params) {
        return new Promise(function (resolve, reject) {
            var id = String(++counter);
            pending[id] = { resolve: resolve, reject: reject };
            var raw;
            try {
                raw = bridge().invoke(id, String(method), JSON.stringify(params || {}));
            } catch (error) {
                delete pending[id];
                reject(error);
                return;
            }
            try {
                var ack = JSON.parse(raw);
                if (ack && ack.error) {
                    delete pending[id];
                    reject(new Error(ack.error));
                }
            } catch (error) {
                delete pending[id];
                reject(new Error("qing-host-protocol"));
            }
        });
    }

    window.__qingResolve = function (id, rawPayload) {
        var entry = pending[id];
        if (!entry) {
            return;
        }
        delete pending[id];
        var payload;
        try {
            payload = JSON.parse(rawPayload);
        } catch (error) {
            entry.reject(new Error("qing-host-protocol"));
            return;
        }
        if (payload && payload.error) {
            entry.reject(new Error(payload.error));
        } else {
            entry.resolve(payload ? payload.data : undefined);
        }
    };

    window.qing = {
        apiVersion: 1,
        host: function () {
            if (!info) {
                info = call("host.info", {});
            }
            return info;
        },
        call: call,
        invoke: invoke,
        can: function (capability) {
            var capabilities = window.qing.host().capabilities || [];
            return capabilities.indexOf(String(capability)) >= 0;
        },
        copy: function (text) {
            return call("clipboard.write", { text: String(text) });
        },
        toast: function (message) {
            return call("toast.show", { text: String(message) });
        }
    };
})();
