//! Finite, plain-text placeholders. No expressions, recursive expansion or HTML.
use crate::{
    ambient::{clock_label, LocalTime},
    providers::codex::worker::AccountSnapshot,
    settings::Settings,
};
use std::collections::BTreeMap;

pub const MAX_RENDERED_CHARS: usize = 96;
pub const PLACEHOLDERS: &[(&str, &str)] = &[
    ("time", "时间"),
    ("date", "日期"),
    ("codex.remaining", "Codex 剩余额度"),
    ("codex.reset", "Codex 距重置"),
    ("codex.primary.remaining", "主额度剩余"),
    ("codex.primary.reset", "主额度距重置"),
    ("codex.secondary.remaining", "次额度剩余"),
    ("codex.secondary.reset", "次额度距重置"),
    ("codex.updated", "最近读取时间"),
];
pub type Values = BTreeMap<String, Option<String>>;

pub fn values(
    settings: &Settings,
    time: Option<LocalTime>,
    account: Option<&AccountSnapshot>,
    now_ms: u64,
) -> Values {
    let mut result: Values = PLACEHOLDERS
        .iter()
        .map(|(key, _)| ((*key).into(), None))
        .collect();
    if let Some(time) = time {
        result.insert(
            "time".into(),
            Some(clock_label(
                time,
                settings.show_seconds,
                settings.clock_24_hour,
            )),
        );
        result.insert(
            "date".into(),
            Some(format!(
                "{:04}-{:02}-{:02}",
                time.year, time.month, time.day
            )),
        );
    }
    // Visibility covers built-in rows AND template values. Acquisition is independent.
    let account = account.filter(|_| settings.show_codex_data);
    if let Some(account) = account {
        if let Some(limits) = account.limits {
            let chosen = [limits.primary, limits.secondary]
                .into_iter()
                .flatten()
                .find(|w| w.used_fraction.is_some())
                .or(limits.primary)
                .or(limits.secondary);
            for (prefix, window) in [
                ("codex", chosen),
                ("codex.primary", limits.primary),
                ("codex.secondary", limits.secondary),
            ] {
                result.insert(
                    format!("{prefix}.remaining"),
                    window
                        .and_then(|w| w.remaining_percent())
                        .map(|v| format!("{v:.0}%")),
                );
                result.insert(
                    format!("{prefix}.reset"),
                    window.and_then(|w| w.reset_in(now_ms / 1000)),
                );
            }
        }
        result.insert(
            "codex.updated".into(),
            account.updated_at_ms.and_then(format_timestamp),
        );
    }
    result
}

#[cfg(windows)]
fn format_timestamp(ms: u64) -> Option<String> {
    use windows_sys::Win32::{
        Foundation::{FILETIME, SYSTEMTIME},
        System::Time::{FileTimeToSystemTime, SystemTimeToTzSpecificLocalTime},
    };
    let ticks = ms
        .checked_mul(10_000)?
        .checked_add(116_444_736_000_000_000)?;
    let file = FILETIME {
        dwLowDateTime: ticks as u32,
        dwHighDateTime: (ticks >> 32) as u32,
    };
    let mut utc = SYSTEMTIME::default();
    let mut local = SYSTEMTIME::default();
    unsafe {
        if FileTimeToSystemTime(&file, &mut utc) == 0
            || SystemTimeToTzSpecificLocalTime(std::ptr::null(), &utc, &mut local) == 0
        {
            return None;
        }
    }
    Some(format!(
        "{:02}-{:02} {:02}:{:02}",
        local.wMonth, local.wDay, local.wHour, local.wMinute
    ))
}
#[cfg(not(windows))]
fn format_timestamp(_ms: u64) -> Option<String> {
    None
}

pub fn render(template: &str, values: &Values, fallback: &str) -> String {
    let mut rest = template;
    let mut output = String::new();
    while !rest.is_empty() {
        if rest.starts_with("{{") {
            output.push('{');
            rest = &rest[2..];
            continue;
        }
        if rest.starts_with("}}") {
            output.push('}');
            rest = &rest[2..];
            continue;
        }
        if rest.starts_with('{') {
            if let Some(end) = rest.find('}') {
                let body = &rest[1..end];
                let (key, local_fallback) = body
                    .split_once('|')
                    .map(|(key, text)| (key.trim(), Some(text)))
                    .unwrap_or((body.trim(), None));
                if let Some(value) = values.get(key) {
                    output.push_str(
                        value
                            .as_deref()
                            .unwrap_or(local_fallback.unwrap_or(fallback)),
                    );
                    rest = &rest[end + 1..];
                    continue;
                }
            }
        }
        let c = rest.chars().next().unwrap();
        output.push(c);
        rest = &rest[c.len_utf8()..];
    }
    output.chars().take(MAX_RENDERED_CHARS).collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::providers::codex::protocol::RateLimitWindow;
    #[test]
    fn data_missing_hidden_and_zero_are_distinct() {
        let mut settings = Settings::default();
        let account = AccountSnapshot {
            limits: Some(crate::providers::codex::protocol::RateLimits {
                primary: Some(RateLimitWindow {
                    used_fraction: Some(1.0),
                    resets_at: Some(3600),
                    ..Default::default()
                }),
                secondary: None,
            }),
            ..Default::default()
        };
        let known = values(&settings, None, Some(&account), 0);
        assert_eq!(
            render("余量 {codex.remaining} / {codex.reset}", &known, "未知"),
            "余量 0% / 1小时0分"
        );
        settings.show_codex_data = false;
        let hidden = values(&settings, None, Some(&account), 0);
        assert_eq!(
            render(
                "{codex.remaining|未显示} {codex.secondary.remaining}",
                &hidden,
                "暂无数据"
            ),
            "未显示 暂无数据"
        );
    }
    #[test]
    fn literals_unicode_and_unknown_keys_are_not_executed() {
        let empty = values(&Settings::default(), None, None, 0);
        assert_eq!(
            render("{{time}} {bad} {codex.remaining|休息🌟}", &empty, ""),
            "{time} {bad} 休息🌟"
        );
        assert_eq!(
            render("{codex.remaining}", &empty, "<b>{date}</b>"),
            "<b>{date}</b>"
        );
        assert_eq!(
            render(
                "{codex.remaining|{bad}} {bad|{codex.remaining}}",
                &empty,
                ""
            ),
            "{bad} {bad|}"
        );
        assert_eq!(
            render(&"🌟".repeat(256), &empty, "").chars().count(),
            MAX_RENDERED_CHARS
        );
    }
    #[test]
    fn secondary_is_not_invented_and_primary_falls_back_only_when_unknown() {
        let limits = crate::providers::codex::protocol::RateLimits::parse(
            &serde_json::json!({"primary":{"resetsAt":400},"secondary":{"usedPercent":25}}),
        );
        let account = AccountSnapshot {
            limits: Some(limits),
            ..Default::default()
        };
        let v = values(&Settings::default(), None, Some(&account), 0);
        assert_eq!(
            render("{codex.remaining} {codex.primary.remaining}", &v, "未知"),
            "75% 未知"
        );
    }
}
