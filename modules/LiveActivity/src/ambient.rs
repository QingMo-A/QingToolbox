//! User-owned clock/text content is not a task and never enters the broker.
use crate::settings::Settings;
use serde::Serialize;

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AmbientContent {
    pub icon: Option<String>,
    pub clock: Option<String>,
    pub date: String,
    pub text: String,
    pub peek_text: Option<String>,
    pub expanded_text: Option<String>,
}
impl AmbientContent {
    pub fn label(&self) -> &str {
        &self.text
    }
}

#[derive(Debug, Clone, Copy)]
pub struct LocalTime {
    pub year: u16,
    pub month: u16,
    pub day: u16,
    pub weekday: u16,
    pub hour: u16,
    pub minute: u16,
    pub second: u16,
}

pub fn clock_label(time: LocalTime, seconds: bool, twenty_four_hour: bool) -> String {
    let hour = if twenty_four_hour {
        time.hour
    } else {
        match time.hour % 12 {
            0 => 12,
            value => value,
        }
    };
    let mut value = if twenty_four_hour {
        format!("{hour:02}:{:02}", time.minute)
    } else {
        format!("{hour}:{:02}", time.minute)
    };
    if seconds {
        value.push_str(&format!(":{:02}", time.second));
    }
    if !twenty_four_hour {
        value.push_str(if time.hour < 12 { " AM" } else { " PM" });
    }
    value
}

pub fn content_at(settings: &Settings, time: Option<LocalTime>) -> Option<AmbientContent> {
    content_with_data(settings, time, None)
}

pub fn content_with_data(
    settings: &Settings,
    time: Option<LocalTime>,
    account: Option<&crate::providers::codex::worker::AccountSnapshot>,
) -> Option<AmbientContent> {
    content_with_runtime(settings, time, account, None, None)
}

/// The broker's ordered view, as far as templates are concerned.
pub struct Tasks<'a> {
    pub focus: Option<&'a crate::activity::LiveActivity>,
    pub stack: &'a [crate::activity::LiveActivity],
    /// Visible plus overflow.
    pub total: usize,
}

/// Render the user's three templates against every data source.
///
/// Sources (clock, timers, tasks, Codex) only fill placeholders. Nothing is
/// appended on their behalf: a running countdown or a new task never adds
/// words the user did not write into a template.
pub fn content_with_runtime(
    settings: &Settings,
    time: Option<LocalTime>,
    account: Option<&crate::providers::codex::worker::AccountSnapshot>,
    timers: Option<&crate::timers::Snapshot>,
    tasks: Option<&Tasks>,
) -> Option<AmbientContent> {
    if settings.icon_text.is_empty()
        && settings.right_text.is_empty()
        && settings.custom_text.is_empty()
        && settings.peek_text.is_empty()
        && settings.expanded_text.is_empty()
    {
        return None;
    }
    let mut values =
        crate::text_template::values(settings, time, account, crate::activity::now_millis());
    apply_timer_values(timers, &mut values);
    if let Some(tasks) = tasks {
        crate::text_template::apply_task_values(&mut values, tasks.focus, tasks.stack, tasks.total);
    }
    let optional_text = |template: &str| {
        (!template.is_empty())
            .then(|| {
                crate::text_template::render(template, &values, &settings.placeholder_fallback)
            })
            .filter(|text| !text.is_empty())
    };
    let peek_text = optional_text(&settings.peek_text);
    let expanded_text = optional_text(&settings.expanded_text);
    let clock = optional_text(&settings.right_text);
    let icon = if matches!(
        settings.icon_text.as_str(),
        "{icon.clock}" | "{icon.status}"
    ) {
        Some(settings.icon_text.clone())
    } else {
        optional_text(&settings.icon_text)
    };
    let text = crate::text_template::render(
        &settings.custom_text,
        &values,
        &settings.placeholder_fallback,
    );
    if icon.is_none()
        && clock.is_none()
        && text.is_empty()
        && peek_text.is_none()
        && expanded_text.is_none()
    {
        return None;
    }
    Some(AmbientContent {
        icon,
        clock,
        date: String::new(),
        text,
        peek_text,
        expanded_text,
    })
}

pub fn apply_timer_values(
    timers: Option<&crate::timers::Snapshot>,
    values: &mut crate::text_template::Values,
) {
    if let Some(timers) = timers {
        for (key, view) in [
            ("stopwatch", &timers.stopwatch),
            ("countdown", &timers.countdown),
        ] {
            // The reading is always available (a fresh countdown reads as its
            // configured length); the state word says whether it is moving.
            values.insert(key.into(), Some(view.text.clone()));
            let state = if view.finished {
                "已结束"
            } else if view.running {
                "进行中"
            } else if view.started {
                "已暂停"
            } else {
                "未开始"
            };
            values.insert(format!("{key}.state"), Some(state.to_owned()));
        }
    }
}

pub fn current(settings: &Settings) -> Option<AmbientContent> {
    content_at(settings, local_time())
}

pub fn current_with_data(
    settings: &Settings,
    account: Option<&crate::providers::codex::worker::AccountSnapshot>,
) -> Option<AmbientContent> {
    content_with_data(settings, local_time(), account)
}

pub fn local_time() -> Option<LocalTime> {
    #[cfg(windows)]
    let time = {
        let mut time = windows_sys::Win32::Foundation::SYSTEMTIME::default();
        unsafe {
            windows_sys::Win32::System::SystemInformation::GetLocalTime(&mut time);
        }
        Some(LocalTime {
            year: time.wYear,
            month: time.wMonth,
            day: time.wDay,
            weekday: time.wDayOfWeek,
            hour: time.wHour,
            minute: time.wMinute,
            second: time.wSecond,
        })
    };
    #[cfg(not(windows))]
    let time = None;
    time
}

#[cfg(test)]
mod tests {
    use super::*;
    fn time(hour: u16) -> LocalTime {
        LocalTime {
            year: 2026,
            month: 10,
            day: 6,
            weekday: 2,
            hour,
            minute: 5,
            second: 9,
        }
    }
    #[test]
    fn local_clock_formats_midnight_noon_and_optional_seconds() {
        assert_eq!(clock_label(time(0), false, true), "00:05");
        assert_eq!(clock_label(time(0), true, false), "12:05:09 AM");
        assert_eq!(clock_label(time(12), false, false), "12:05 PM");
        assert_eq!(clock_label(time(23), true, true), "23:05:09");
    }
    #[test]
    fn blank_content_hides_but_text_does_not_require_a_clock() {
        let mut settings = Settings {
            show_clock: false,
            peek_text: String::new(),
            expanded_text: String::new(),
            ..Settings::default()
        };
        assert_eq!(content_at(&settings, Some(time(0))), None);
        settings.custom_text = "专注当下".into();
        let content = content_at(&settings, None).unwrap();
        assert_eq!(content.label(), "专注当下");
        assert!(content.clock.is_none());
    }
    #[test]
    fn state_templates_use_the_same_real_values_and_visibility_fallbacks() {
        let mut settings = Settings {
            show_clock: false,
            custom_text: "常驻".into(),
            peek_text: "{time} · {codex.remaining|未接入}".into(),
            expanded_text: "{date}\n余量 {codex.remaining}".into(),
            placeholder_fallback: "暂无额度".into(),
            ..Default::default()
        };
        let account = crate::providers::codex::worker::AccountSnapshot {
            limits: Some(crate::providers::codex::protocol::RateLimits {
                primary: Some(crate::providers::codex::protocol::RateLimitWindow {
                    used_fraction: Some(0.25),
                    ..Default::default()
                }),
                secondary: None,
            }),
            ..Default::default()
        };
        let content = content_with_data(&settings, Some(time(10)), Some(&account)).unwrap();
        assert_eq!(content.text, "常驻");
        assert_eq!(content.peek_text.as_deref(), Some("10:05 · 75%"));
        assert_eq!(
            content.expanded_text.as_deref(),
            Some("2026-10-06\n余量 75%")
        );
        settings.show_codex_data = false;
        let hidden = content_with_data(&settings, Some(time(10)), Some(&account)).unwrap();
        assert_eq!(hidden.peek_text.as_deref(), Some("10:05 · 未接入"));
        assert_eq!(
            hidden.expanded_text.as_deref(),
            Some("2026-10-06\n余量 暂无额度")
        );
        settings.custom_text.clear();
        settings.expanded_text.clear();
        assert!(
            content_at(&settings, None).is_some(),
            "peek-only content needs a compact entry"
        );
        settings.peek_text.clear();
        assert!(content_at(&settings, None).is_none());
    }
    #[test]
    fn minute_clock_does_not_change_on_each_second() {
        let settings = Settings {
            custom_text: "{date} · 周{weekday}".into(),
            right_text: "{time}".into(),
            ..Default::default()
        };
        let mut later = time(10);
        later.second = 40;
        assert_eq!(
            content_at(&settings, Some(time(10))),
            content_at(&settings, Some(later))
        );
        assert!(content_at(&settings, Some(later))
            .unwrap()
            .text
            .contains("周二"));
    }
    #[test]
    fn every_visible_slot_is_explicit_and_clearable() {
        assert!(content_at(&Settings::default(), Some(time(10))).is_none());
        let mut settings = Settings {
            icon_text: "★".into(),
            right_text: "右 {time}".into(),
            ..Default::default()
        };
        let content = content_at(&settings, Some(time(10))).unwrap();
        assert_eq!(content.label(), "");
        assert_eq!(content.icon.as_deref(), Some("★"));
        assert_eq!(content.clock.as_deref(), Some("右 10:05"));
        assert!(
            content.date.is_empty()
                && content.peek_text.is_none()
                && content.expanded_text.is_none()
        );
        settings.icon_text = "{icon.status}".into();
        assert_eq!(
            content_at(&settings, Some(time(10)))
                .unwrap()
                .icon
                .as_deref(),
            Some("{icon.status}")
        );
        settings.icon_text.clear();
        settings.right_text.clear();
        assert!(content_at(&settings, Some(time(10))).is_none());
    }
}
