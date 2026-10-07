//! User-owned clock/text content is not a task and never enters the broker.
use crate::settings::Settings;
use serde::Serialize;

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AmbientContent {
    pub clock: Option<String>,
    pub date: String,
    pub text: String,
    pub peek_text: Option<String>,
    pub expanded_text: Option<String>,
}
impl AmbientContent {
    pub fn label(&self) -> &str {
        if !self.text.is_empty() {
            &self.text
        } else if !self.date.is_empty() {
            &self.date
        } else {
            "Qing Island"
        }
    }
    pub fn detail(&self) -> &str {
        if !self.text.is_empty() {
            &self.text
        } else {
            &self.date
        }
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
    content_with_runtime(settings, time, account, None)
}

pub fn content_with_runtime(
    settings: &Settings,
    time: Option<LocalTime>,
    account: Option<&crate::providers::codex::worker::AccountSnapshot>,
    timers: Option<&crate::timers::Snapshot>,
) -> Option<AmbientContent> {
    if !settings.show_clock
        && settings.custom_text.is_empty()
        && settings.peek_text.is_empty()
        && settings.expanded_text.is_empty()
        && timers.is_none()
    {
        return None;
    }
    let clock = time
        .filter(|_| settings.show_clock)
        .map(|time| clock_label(time, settings.show_seconds, settings.clock_24_hour));
    let date = time
        .filter(|_| settings.show_clock)
        .map(|time| {
            let weekdays = ["日", "一", "二", "三", "四", "五", "六"];
            format!(
                "{:04}-{:02}-{:02} · 周{}",
                time.year,
                time.month,
                time.day,
                weekdays.get(time.weekday as usize).unwrap_or(&"?")
            )
        })
        .unwrap_or_default();
    let mut values =
        crate::text_template::values(settings, time, account, crate::activity::now_millis());
    apply_timer_values(settings, timers, &mut values);
    let optional_text = |template: &str| {
        (!template.is_empty()).then(|| {
            crate::text_template::render(template, &values, &settings.placeholder_fallback)
        })
    };
    let peek_text = optional_text(&settings.peek_text);
    let expanded_text = optional_text(&settings.expanded_text);
    let mut text = crate::text_template::render(
        &settings.custom_text,
        &values,
        &settings.placeholder_fallback,
    );
    if let Some(timers) = timers {
        for (key, label, view, show) in [
            (
                "stopwatch",
                "计时",
                &timers.stopwatch,
                settings.show_stopwatch,
            ),
            (
                "countdown",
                "倒计时",
                &timers.countdown,
                settings.show_countdown,
            ),
        ] {
            if show
                && view.started
                && (view.finished || !crate::text_template::uses(&settings.custom_text, key))
            {
                if !text.is_empty() {
                    text.push_str(" · ");
                }
                if view.finished {
                    text.push_str("倒计时结束");
                } else {
                    text.push_str(&format!(
                        "{label} {}{}",
                        view.text,
                        if view.running { "" } else { "（已暂停）" }
                    ));
                }
            }
        }
    }
    if clock.is_none() && text.is_empty() && peek_text.is_none() && expanded_text.is_none() {
        return None;
    }
    Some(AmbientContent {
        clock,
        date,
        text,
        peek_text,
        expanded_text,
    })
}

pub fn apply_timer_values(
    settings: &Settings,
    timers: Option<&crate::timers::Snapshot>,
    values: &mut crate::text_template::Values,
) {
    if let Some(timers) = timers {
        for (key, view, show) in [
            ("stopwatch", &timers.stopwatch, settings.show_stopwatch),
            ("countdown", &timers.countdown, settings.show_countdown),
        ] {
            values.insert(
                key.into(),
                (show && view.started).then(|| view.text.clone()),
            );
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
        let settings = Settings::default();
        let mut later = time(10);
        later.second = 40;
        assert_eq!(
            content_at(&settings, Some(time(10))),
            content_at(&settings, Some(later))
        );
        assert!(content_at(&settings, Some(later))
            .unwrap()
            .date
            .contains("周二"));
    }
}
