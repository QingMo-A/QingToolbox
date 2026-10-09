//! Finite, plain-text placeholders. No expressions, recursive expansion or HTML.
use crate::{
    activity::{ActivityState, LiveActivity, ProviderKind},
    ambient::{clock_label, LocalTime},
    providers::codex::worker::AccountSnapshot,
    settings::Settings,
};
use std::collections::BTreeMap;

pub const MAX_RENDERED_CHARS: usize = 96;
/// Every data source reaches the island only through these keys. Nothing is
/// ever appended to the user's text on a source's behalf: a value shows up
/// exactly where, and only if, a template asks for it.
pub const PLACEHOLDERS: &[(&str, &str)] = &[
    ("time", "时间"),
    ("date", "日期"),
    ("stopwatch", "计时器"),
    ("stopwatch.state", "计时器状态"),
    ("countdown", "倒计时"),
    ("countdown.state", "倒计时状态"),
    ("task", "当前任务"),
    ("task.state", "任务状态"),
    ("task.detail", "任务说明"),
    ("task.progress", "任务进度"),
    ("task.source", "任务来源"),
    ("tasks", "任务列表"),
    ("tasks.count", "任务数量"),
    ("codex.working", "Codex 工作中"),
    ("codex.waiting", "Codex 等待中"),
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
        // Thread counts are only known through a shared app-server; an
        // account-only connection cannot see desktop tasks, so it says nothing.
        if account.connection_mode.as_deref() == Some("shared") {
            result.insert(
                "codex.working".into(),
                Some(account.working_threads.to_string()),
            );
            result.insert(
                "codex.waiting".into(),
                Some(account.waiting_threads.to_string()),
            );
        }
    }
    result
}

/// The words a task's state reads as, shared by `{task.state}` and `{tasks}`.
pub fn state_label(state: ActivityState) -> &'static str {
    match state {
        ActivityState::Running => "运行中",
        ActivityState::Waiting => "需要你处理",
        ActivityState::Paused => "已暂停",
        ActivityState::Success => "已完成",
        ActivityState::Failed => "失败",
        ActivityState::Idle => "空闲",
        ActivityState::Cancelled => "已取消",
        ActivityState::Unknown => "未知",
    }
}

fn source_label(provider: ProviderKind) -> &'static str {
    match provider {
        ProviderKind::Codex => "Codex",
        ProviderKind::Mock => "模拟",
        ProviderKind::Media => "媒体",
        ProviderKind::Transfer => "文件",
    }
}

fn percent(activity: &LiveActivity) -> Option<String> {
    activity
        .progress
        .as_ref()
        .and_then(|progress| progress.fraction())
        .map(|fraction| format!("{:.0}%", fraction.clamp(0.0, 1.0) * 100.0))
}

/// Task placeholders from the broker's ordered view. `focus` is the task the
/// island is about; `stack` the visible ones; `total` includes overflow.
pub fn apply_task_values(
    values: &mut Values,
    focus: Option<&LiveActivity>,
    stack: &[LiveActivity],
    total: usize,
) {
    values.insert("task".into(), focus.map(|task| task.title.clone()));
    values.insert(
        "task.state".into(),
        focus.map(|task| state_label(task.state).to_owned()),
    );
    values.insert(
        "task.detail".into(),
        focus.and_then(|task| task.subtitle.clone()),
    );
    values.insert("task.progress".into(), focus.and_then(percent));
    values.insert(
        "task.source".into(),
        focus.map(|task| source_label(task.provider).to_owned()),
    );
    let lines = stack
        .iter()
        .take(3)
        .map(|task| {
            format!(
                "{} · {}",
                task.title,
                percent(task).unwrap_or_else(|| state_label(task.state).to_owned())
            )
        })
        .collect::<Vec<_>>();
    values.insert(
        "tasks".into(),
        (!lines.is_empty()).then(|| lines.join("\n")),
    );
    values.insert("tasks.count".into(), Some(total.to_string()));
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

/// Same finite grammar as rendering: escaped/unknown-prefix tokens are not data.
pub fn uses(template: &str, key: &str) -> bool {
    let mut rest = template;
    while !rest.is_empty() {
        if rest.starts_with("{{") || rest.starts_with("}}") {
            rest = &rest[2..];
            continue;
        }
        if rest.starts_with('{') {
            if let Some(end) = rest.find('}') {
                let body = &rest[1..end];
                let name = body
                    .split_once('|')
                    .map(|(name, _)| name)
                    .unwrap_or(body)
                    .trim();
                if name == key {
                    return true;
                }
                if PLACEHOLDERS.iter().any(|(known, _)| *known == name) {
                    rest = &rest[end + 1..];
                    continue;
                }
            }
        }
        rest = &rest[rest.chars().next().unwrap().len_utf8()..];
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::providers::codex::protocol::RateLimitWindow;
    #[test]
    fn token_presence_matches_rendering_not_raw_prefixes() {
        assert!(uses("{ time |未知}", "time"));
        assert!(uses("{date}\n{stopwatch}", "stopwatch"));
        assert!(!uses("{{time}} {timer} {timeXYZ}", "time"));
        assert!(!uses("{codex.remaining|{time}}", "time"));
    }
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
    fn tasks_reach_the_text_only_through_their_placeholders() {
        use crate::activity::{ActivityProgress, LiveActivity, ProviderKind};
        let export = LiveActivity::running("e", ProviderKind::Mock, "demo", "导出")
            .with_subtitle("压缩素材")
            .with_progress(ActivityProgress::determinate(7.0, 20.0));
        let approval = LiveActivity::running("a", ProviderKind::Codex, "thread", "审批")
            .with_state(ActivityState::Waiting);
        let mut v = values(&Settings::default(), None, None, 0);
        apply_task_values(&mut v, Some(&approval), &[approval.clone(), export], 5);
        assert_eq!(
            render(
                "{task} {task.state} {task.source} {task.progress|无进度} 共 {tasks.count}",
                &v,
                "?"
            ),
            "审批 需要你处理 Codex 无进度 共 5"
        );
        assert_eq!(render("{tasks}", &v, ""), "审批 · 需要你处理\n导出 · 35%");
        let mut idle = values(&Settings::default(), None, None, 0);
        apply_task_values(&mut idle, None, &[], 0);
        assert_eq!(render("{task|空闲} {tasks.count}", &idle, "?"), "空闲 0");
    }
    #[test]
    fn codex_thread_counts_need_a_shared_connection() {
        let shared = AccountSnapshot {
            connection_mode: Some("shared".into()),
            working_threads: 2,
            waiting_threads: 1,
            ..Default::default()
        };
        let v = values(&Settings::default(), None, Some(&shared), 0);
        assert_eq!(render("{codex.working}/{codex.waiting}", &v, "?"), "2/1");
        let only_quota = AccountSnapshot {
            connection_mode: Some("accountOnly".into()),
            ..shared
        };
        let v = values(&Settings::default(), None, Some(&only_quota), 0);
        assert_eq!(render("{codex.working|未接入}", &v, "?"), "未接入");
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
