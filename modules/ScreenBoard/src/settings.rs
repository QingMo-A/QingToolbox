use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default, deny_unknown_fields)]
pub struct Settings {
    pub color: String,
    pub width: f64,
    pub font_size: f64,
    pub text: String,
    pub expression: String,
    pub x_min: f64,
    pub x_max: f64,
    pub y_min: f64,
    pub y_max: f64,
    pub grid: bool,
    pub tool: String,
    pub theme: String,
    pub appearance_preset: String,
}
impl Default for Settings {
    fn default() -> Self {
        Self {
            color: "#3478f6".into(),
            width: 4.0,
            font_size: 30.0,
            text: String::new(),
            expression: "sin(x)".into(),
            x_min: -10.0,
            x_max: 10.0,
            y_min: -5.0,
            y_max: 5.0,
            grid: true,
            tool: "pen".into(),
            theme: "system".into(),
            appearance_preset: "qing-default".into(),
        }
    }
}
impl Settings {
    pub fn validate(&self) -> Result<(), String> {
        if self.color.len() != 7
            || !self.color.starts_with('#')
            || !self.color[1..].bytes().all(|b| b.is_ascii_hexdigit())
        {
            return Err("颜色须为 #RRGGBB。".into());
        }
        if !self.width.is_finite()
            || !(1.0..=40.0).contains(&self.width)
            || !self.font_size.is_finite()
            || !(10.0..=160.0).contains(&self.font_size)
        {
            return Err("线宽或字号超出范围。".into());
        }
        if self.text.chars().count() > 2000 || self.expression.len() > 256 {
            return Err("文字或函数表达式过长。".into());
        }
        for (lo, hi) in [(self.x_min, self.x_max), (self.y_min, self.y_max)] {
            if !lo.is_finite()
                || !hi.is_finite()
                || lo.abs() > 1e6
                || hi.abs() > 1e6
                || !(0.001..=1e6).contains(&(hi - lo))
            {
                return Err("坐标范围必须有限，且最大值大于最小值。".into());
            }
        }
        if ![
            "pen", "line", "curve", "rect", "ellipse", "text", "graph", "select", "eraser",
        ]
        .contains(&self.tool.as_str())
        {
            return Err("未知画板工具。".into());
        }
        if !["system", "light", "dark"].contains(&self.theme.as_str())
            || ![
                "qing-default",
                "neon-circuit",
                "greenline",
                "aurora-flow",
                "qing-nova",
            ]
            .contains(&self.appearance_preset.as_str())
        {
            return Err("未知主题。".into());
        }
        Ok(())
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn defaults_valid() {
        assert!(Settings::default().validate().is_ok());
    }
    #[test]
    fn rejects_nonfinite_and_bad_ranges() {
        let mut s = Settings::default();
        s.width = f64::NAN;
        assert!(s.validate().is_err());
        s.width = 4.;
        s.x_min = s.x_max;
        assert!(s.validate().is_err());
    }
    #[test]
    fn rejects_unbounded_text_and_invalid_color() {
        let mut s = Settings::default();
        s.text = "a".repeat(2001);
        assert!(s.validate().is_err());
        s.text.clear();
        s.color = "#🔴".into();
        assert!(s.validate().is_err());
    }
}
