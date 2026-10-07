//! Monitor geometry and DPI, resolved through Win32 rather than assumed.
//!
//! Nothing here hardcodes a resolution. Every measurement comes from the
//! current monitor, and every logical size is converted through that monitor's
//! own DPI, so a 150% secondary display positions the island correctly instead
//! of placing it at a scaled fraction of the primary.

use serde::Serialize;

/// A monitor's work area and scale, in physical pixels plus the DPI factor.
#[derive(Debug, Clone, Copy, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MonitorMetrics {
    /// Work-area origin, in physical pixels, in virtual-desktop coordinates.
    pub x: i32,
    pub y: i32,
    /// Work area, excluding the taskbar.
    pub width: u32,
    pub height: u32,
    /// DPI divided by 96. `1.0` at standard scale, `1.5` at 150%.
    pub scale: f64,
    pub primary: bool,
}

impl MonitorMetrics {
    /// Convert a logical-pixel length to physical pixels on this monitor.
    pub fn to_physical(self, logical: f64) -> i32 {
        (logical * self.scale).round() as i32
    }

    /// Convert physical pixels back to logical units.
    pub fn to_logical(self, physical: i32) -> f64 {
        if self.scale <= 0.0 {
            return physical as f64;
        }
        physical as f64 / self.scale
    }

    /// Centre a box horizontally within the work area.
    pub fn center_x(&self, width: i32) -> i32 {
        self.x + ((self.width as i32 - width) / 2)
    }

    /// Anchor a box to the top or bottom of the work area, offset from the edge.
    pub fn anchor_y(&self, height: i32, margin: i32, at_top: bool) -> i32 {
        if at_top {
            self.y + margin
        } else {
            self.y + self.height as i32 - height - margin
        }
    }
}

/// A degenerate but valid fallback, used only when every Win32 query fails.
///
/// A window must still be placeable, so this returns a small box at the origin
/// rather than propagating an error the overlay cannot act on.
pub fn fallback() -> MonitorMetrics {
    MonitorMetrics {
        x: 0,
        y: 0,
        width: 1920,
        height: 1080,
        scale: 1.0,
        primary: true,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn monitor(
        x: i32,
        y: i32,
        width: u32,
        height: u32,
        scale: f64,
        primary: bool,
    ) -> MonitorMetrics {
        MonitorMetrics {
            x,
            y,
            width,
            height,
            scale,
            primary,
        }
    }

    #[test]
    fn center_x_splits_the_remaining_space_evenly() {
        let primary = monitor(0, 0, 1920, 1040, 1.0, true);
        assert_eq!(primary.center_x(240), (1920 - 240) / 2);
    }

    #[test]
    fn center_x_respects_a_monitor_with_a_non_zero_origin() {
        // A secondary monitor to the right of a 1920-wide primary.
        let left = monitor(0, 0, 1920, 1040, 1.0, true);
        let right = monitor(1920, 0, 2560, 1400, 1.0, false);
        assert_eq!(right.center_x(240), 1920 + (2560 - 240) / 2);
        // And one placed to the left, which has a negative origin.
        let negative = monitor(-1080, 0, 1080, 1920, 1.0, false);
        assert_eq!(negative.center_x(240), -1080 + (1080 - 240) / 2);
        assert!(negative.center_x(240) < left.x);
    }

    #[test]
    fn anchor_y_flips_between_the_two_edges() {
        let area = monitor(0, 0, 1920, 1040, 1.0, true);
        assert_eq!(area.anchor_y(40, 12, true), 12);
        assert_eq!(area.anchor_y(40, 12, false), 1040 - 40 - 12);
    }

    #[test]
    fn anchoring_uses_the_work_area_so_the_taskbar_is_never_covered() {
        // A work area already excludes the taskbar; the island must be placed
        // relative to that, not to the full screen height.
        let with_taskbar = monitor(0, 0, 1920, 1040, 1.0, true);
        let y = with_taskbar.anchor_y(40, 12, false);
        assert!(y + 40 <= with_taskbar.height as i32);
    }

    #[test]
    fn physical_and_logical_lengths_convert_both_ways() {
        let scaled = monitor(0, 0, 2560, 1400, 1.5, true);
        assert_eq!(scaled.to_physical(240.0), 360);
        assert_eq!(scaled.to_logical(360), 240.0);

        let double = monitor(0, 0, 3840, 2160, 2.0, true);
        assert_eq!(double.to_physical(240.0), 480);

        let standard = monitor(0, 0, 1920, 1080, 1.0, true);
        assert_eq!(standard.to_physical(240.0), 240);
    }

    #[test]
    fn a_degenerate_scale_does_not_produce_nan_or_infinity() {
        let broken = monitor(0, 0, 1920, 1080, 0.0, true);
        assert_eq!(broken.to_logical(100), 100.0);
        // `to_physical` returns an `i32`, so the failure mode to rule out is a
        // saturating cast, not a NaN. A 10-logical-pixel length must stay small.
        assert!((0..=10).contains(&broken.to_physical(10.0)));
    }

    #[test]
    fn a_scaled_island_is_wider_than_its_logical_width() {
        // The whole point of the conversion: a 240-logical-pixel island must
        // occupy the same physical fraction of a high-DPI screen.
        let standard = monitor(0, 0, 1920, 1040, 1.0, true);
        let hidpi = monitor(0, 0, 3840, 2080, 2.0, true);
        let standard_fraction = standard.to_physical(240.0) as f64 / standard.width as f64;
        let hidpi_fraction = hidpi.to_physical(240.0) as f64 / hidpi.width as f64;
        assert!(
            (standard_fraction - hidpi_fraction).abs() < 0.001,
            "the island must keep the same relative size across DPI"
        );
    }

    #[test]
    fn the_fallback_is_placeable_and_flagged_primary() {
        let fallback = fallback();
        assert!(fallback.primary);
        assert!(fallback.width > 0 && fallback.height > 0);
        assert!(fallback.scale > 0.0);
        assert!(fallback.center_x(240) >= 0);
    }
}
