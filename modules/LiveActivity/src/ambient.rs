//! User-owned clock/text content is not a task and never enters the broker.
use crate::settings::Settings;
use serde::Serialize;

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct AmbientContent {
    pub clock: Option<String>,
    pub date: String,
    pub text: String,
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
    if !settings.show_clock && settings.custom_text.is_empty() {
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
    if clock.is_none() && settings.custom_text.is_empty() {
        return None;
    }
    Some(AmbientContent {
        clock,
        date,
        text: settings.custom_text.clone(),
    })
}

pub fn current(settings: &Settings) -> Option<AmbientContent> {
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
    content_at(settings, time)
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
