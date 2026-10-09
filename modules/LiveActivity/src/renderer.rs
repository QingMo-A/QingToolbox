//! Bounded CPU glass renderer; no browser or continuous GPU frame loop.
#![cfg(windows)]
use crate::{
    activity::ActivityState,
    overlay::{Bounds, IslandModel, IslandState},
    settings::{Settings, SurfaceStyle},
};
use std::time::{Duration, Instant};
use windows_sys::Win32::{
    Foundation::{POINT, RECT, SIZE},
    Graphics::Gdi::*,
    UI::WindowsAndMessaging::{UpdateLayeredWindow, ULW_ALPHA},
};

const BACKGROUND_INTERVAL: Duration = Duration::from_secs(1);

#[derive(Default)]
pub struct Renderer {
    backdrop: Option<Backdrop>,
    last_pixels: Vec<u32>,
    last_bounds: Option<Bounds>,
    pub samples: u64,
    pub sample_micros: u64,
}
struct Backdrop {
    region: Bounds,
    output: Bounds,
    width: usize,
    height: usize,
    raw: Vec<u32>,
    blurred: Vec<u32>,
    sampled: Instant,
}
impl Drop for Backdrop {
    fn drop(&mut self) {
        self.raw.fill(0);
        self.blurred.fill(0);
    }
}
impl Renderer {
    pub fn clear(&mut self) {
        self.backdrop = None;
        self.last_pixels.fill(0);
        self.last_pixels.clear();
        self.last_bounds = None;
    }
    pub fn until_refresh(&self) -> Duration {
        self.backdrop
            .as_ref()
            .map(|b| BACKGROUND_INTERVAL.saturating_sub(b.sampled.elapsed()))
            .unwrap_or_default()
    }
    unsafe fn background(&mut self, bounds: Bounds, scale: f64) -> Result<(), String> {
        if self
            .backdrop
            .as_ref()
            .is_some_and(|b| b.output == bounds && b.sampled.elapsed() < BACKGROUND_INTERVAL)
        {
            return Ok(());
        }
        let started = Instant::now();
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            GetSystemMetrics, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN,
            SM_YVIRTUALSCREEN,
        };
        // The blur reaches about 18 logical pixels; sample past the island so
        // its edges blur real surroundings instead of a clamped border.
        let pad = (34.0 * scale).round().clamp(12.0, 128.0) as i32;
        let vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        let vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        let x = bounds.x.saturating_sub(pad).max(vx);
        let y = bounds.y.saturating_sub(pad).max(vy);
        let right = bounds
            .x
            .saturating_add(bounds.width)
            .saturating_add(pad)
            .min(vx.saturating_add(GetSystemMetrics(SM_CXVIRTUALSCREEN)));
        let bottom = bounds
            .y
            .saturating_add(bounds.height)
            .saturating_add(pad)
            .min(vy.saturating_add(GetSystemMetrics(SM_CYVIRTUALSCREEN)));
        let region = Bounds {
            x,
            y,
            width: right - x,
            height: bottom - y,
        };
        if region.is_empty() {
            return Err("磨砂背景不在可采样的屏幕范围内".into());
        }
        let width = (region.width + 1) / 2;
        let height = (region.height + 1) / 2;
        if (width as usize)
            .checked_mul(height as usize)
            .map_or(true, |n| n > crate::blur::MAX_PIXELS)
        {
            return Err("磨砂区域超过安全尺寸限制".into());
        }
        let sample = Surface::new(width, height)?;
        let desktop = GetDC(std::ptr::null_mut());
        if desktop.is_null() {
            return Err("当前桌面不可采样，已回退半透明".into());
        }
        SetStretchBltMode(sample.dc, HALFTONE);
        SetBrushOrgEx(sample.dc, 0, 0, std::ptr::null_mut());
        let copied = StretchBlt(
            sample.dc,
            0,
            0,
            width,
            height,
            desktop,
            region.x,
            region.y,
            region.width,
            region.height,
            SRCCOPY | CAPTUREBLT,
        );
        ReleaseDC(std::ptr::null_mut(), desktop);
        if copied == 0 {
            return Err("背景采样失败，已回退半透明".into());
        }
        GdiFlush();
        let raw = std::slice::from_raw_parts(sample.pixels, (width * height) as usize)
            .iter()
            .map(|p| p & 0xFFFFFF)
            .collect::<Vec<_>>();
        let blurred = if let Some(previous) = self
            .backdrop
            .as_ref()
            .filter(|b| b.region == region && b.raw == raw)
        {
            previous.blurred.clone()
        } else {
            frost(&raw, width as usize, height as usize, scale)?
        };
        self.samples += 1;
        self.sample_micros = started.elapsed().as_micros() as u64;
        self.backdrop = Some(Backdrop {
            region,
            output: bounds,
            width: width as usize,
            height: height as usize,
            raw,
            blurred,
            sampled: Instant::now(),
        });
        Ok(())
    }
    /// The frosted backdrop at a screen pixel, bilinearly filtered so the
    /// half-resolution sample never shows as blocks.
    fn pixel(&self, x: i32, y: i32) -> Option<u32> {
        let b = self.backdrop.as_ref()?;
        let fx = ((x - b.region.x) as f64 + 0.5) * b.width as f64 / b.region.width as f64 - 0.5;
        let fy = ((y - b.region.y) as f64 + 0.5) * b.height as f64 / b.region.height as f64 - 0.5;
        let fx = fx.clamp(0.0, (b.width - 1) as f64);
        let fy = fy.clamp(0.0, (b.height - 1) as f64);
        let (x0, y0) = (fx.floor() as usize, fy.floor() as usize);
        let (x1, y1) = ((x0 + 1).min(b.width - 1), (y0 + 1).min(b.height - 1));
        let (tx, ty) = (fx - x0 as f64, fy - y0 as f64);
        let at = |x: usize, y: usize| b.blurred[y * b.width + x];
        let top = mix(at(x0, y0), at(x1, y0), tx);
        let bottom = mix(at(x0, y1), at(x1, y1), tx);
        Some(mix(top, bottom, ty))
    }
}
impl Drop for Renderer {
    fn drop(&mut self) {
        self.clear();
    }
}

/// Turn a half-resolution desktop sample into the glass backdrop: a ~9 px
/// half-resolution (~18 logical px) blur, then vibrancy, so colours behind
/// the glass read richer instead of greyer.
fn frost(raw: &[u32], width: usize, height: usize, scale: f64) -> Result<Vec<u32>, String> {
    let mut blurred = crate::blur::gaussian(
        raw,
        width,
        height,
        (9.0 * scale).round().clamp(5.0, 24.0) as usize,
    )
    .map_err(str::to_string)?;
    for pixel in blurred.iter_mut() {
        *pixel = saturate(*pixel, 1.65);
    }
    Ok(blurred)
}

struct Surface {
    dc: HDC,
    bitmap: HBITMAP,
    previous: HGDIOBJ,
    pixels: *mut u32,
    width: i32,
    height: i32,
}
impl Surface {
    unsafe fn new(width: i32, height: i32) -> Result<Self, String> {
        let dc = CreateCompatibleDC(std::ptr::null_mut());
        if dc.is_null() {
            return Err("could not create drawing context".into());
        }
        let info = BITMAPINFO {
            bmiHeader: BITMAPINFOHEADER {
                biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
                biWidth: width,
                biHeight: -height,
                biPlanes: 1,
                biBitCount: 32,
                biCompression: BI_RGB,
                ..Default::default()
            },
            ..Default::default()
        };
        let mut pixels = std::ptr::null_mut();
        let bitmap = CreateDIBSection(
            dc,
            &info,
            DIB_RGB_COLORS,
            &mut pixels,
            std::ptr::null_mut(),
            0,
        );
        if bitmap.is_null() || pixels.is_null() {
            DeleteDC(dc);
            return Err("could not allocate drawing surface".into());
        }
        let previous = SelectObject(dc, bitmap);
        Ok(Self {
            dc,
            bitmap,
            previous,
            pixels: pixels.cast(),
            width,
            height,
        })
    }
    unsafe fn label(&self, text: &str, rect: RECT, font_size: f64, bold: bool, color: u32) {
        self.text(
            text,
            rect,
            font_size,
            bold,
            color,
            DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS | DT_NOPREFIX,
        );
    }
    #[cfg(test)]
    unsafe fn paragraph(&self, text: &str, rect: RECT, font_size: f64, color: u32) {
        self.text(
            text,
            rect,
            font_size,
            false,
            color,
            DT_WORDBREAK | DT_END_ELLIPSIS | DT_NOPREFIX,
        );
    }
    unsafe fn text(
        &self,
        text: &str,
        rect: RECT,
        font_size: f64,
        bold: bool,
        color: u32,
        flags: u32,
    ) {
        // DrawTextW requires a readable text buffer even for an empty string.
        // An empty Vec's as_ptr() is only a Rust dangling sentinel, not a valid
        // Windows string. Empty/temporarily clipped content needs no GDI call.
        if text.is_empty() || rect.right <= rect.left || rect.bottom <= rect.top {
            return;
        }
        let Some(font) = Font::select(self.dc, font_size, bold) else {
            return;
        };
        SetBkMode(self.dc, TRANSPARENT as i32);
        SetTextColor(self.dc, color);
        let text = windows_text_buffer(text);
        let mut rect = rect;
        DrawTextW(
            self.dc,
            text.as_ptr(),
            (text.len() - 1) as i32,
            &mut rect,
            flags,
        );
        drop(font);
    }
    /// The pixel size `text` would occupy, wrapped to `width` when `flags`
    /// asks for word breaks. Nothing is drawn.
    unsafe fn measure(
        &self,
        text: &str,
        width: i32,
        font_size: f64,
        bold: bool,
        flags: u32,
    ) -> (i32, i32) {
        if text.is_empty() || width <= 0 {
            return (0, 0);
        }
        let Some(font) = Font::select(self.dc, font_size, bold) else {
            return (0, 0);
        };
        let text = windows_text_buffer(text);
        let mut rect = RECT {
            left: 0,
            top: 0,
            right: width,
            bottom: 0,
        };
        DrawTextW(
            self.dc,
            text.as_ptr(),
            (text.len() - 1) as i32,
            &mut rect,
            flags | DT_CALCRECT | DT_NOPREFIX,
        );
        drop(font);
        (rect.right - rect.left, rect.bottom - rect.top)
    }
    unsafe fn bar(&self, rect: RECT, color: u32) {
        let brush = CreateSolidBrush(color);
        FillRect(self.dc, &rect, brush);
        DeleteObject(brush);
    }
}

/// A GDI font selected into a DC for one call, restored and freed on drop.
///
/// Emphasis uses the Semibold face: GDI maps a 600 weight inside "Segoe UI" to
/// its Bold face, which reads heavy at 12 px on a 32 px capsule.
struct Font {
    dc: HDC,
    font: HFONT,
    old: HGDIOBJ,
}
impl Font {
    unsafe fn select(dc: HDC, font_size: f64, strong: bool) -> Option<Self> {
        if !font_size.is_finite() || font_size <= 0.0 {
            return None;
        }
        let face: Vec<u16> = if strong {
            "Segoe UI Semibold\0"
        } else {
            "Segoe UI\0"
        }
        .encode_utf16()
        .collect();
        let font = CreateFontW(
            -(font_size.round() as i32),
            0,
            0,
            0,
            if strong { 600 } else { 400 },
            0,
            0,
            0,
            DEFAULT_CHARSET as u32,
            OUT_DEFAULT_PRECIS as u32,
            CLIP_DEFAULT_PRECIS as u32,
            ANTIALIASED_QUALITY as u32,
            DEFAULT_PITCH as u32,
            face.as_ptr(),
        );
        if font.is_null() {
            return None;
        }
        let old = SelectObject(dc, font);
        if old.is_null() || old as isize == GDI_ERROR as isize {
            DeleteObject(font);
            return None;
        }
        Some(Self { dc, font, old })
    }
}
impl Drop for Font {
    fn drop(&mut self) {
        unsafe {
            SelectObject(self.dc, self.old);
            DeleteObject(self.font);
        }
    }
}

fn windows_text_buffer(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(std::iter::once(0)).collect()
}

/// COLORREF is 0x00BBGGRR; the layered DIB uses 0xAARRGGBB.
fn colorref_to_rgb(color: u32) -> u32 {
    ((color & 255) << 16) | (color & 0xFF00) | ((color >> 16) & 255)
}

/// GDI's RGB brightness is not alpha: dark glyphs must be as opaque as white
/// glyphs. Render coverage separately, then store premultiplied ARGB ink.
///
/// `reveal` scales every later stroke's opacity. The capsule's own header is
/// drawn at full strength; content that only exists in the target state fades
/// in while the window is still growing towards it.
struct Foreground {
    surface: Surface,
    coverage: Surface,
    reveal: std::cell::Cell<f64>,
    /// A soft drop shadow under glyphs: (offset in pixels, opacity). Used on
    /// jelly, whose vivid gel can sit close to the text's own brightness.
    shadow: std::cell::Cell<(i32, f64)>,
}
impl Foreground {
    unsafe fn new(width: i32, height: i32) -> Result<Self, String> {
        let surface = Surface::new(width, height)?;
        std::slice::from_raw_parts_mut(surface.pixels, (width * height) as usize).fill(0);
        Ok(Self {
            surface,
            coverage: Surface::new(width, height)?,
            reveal: std::cell::Cell::new(1.0),
            shadow: std::cell::Cell::new((0, 0.0)),
        })
    }
    unsafe fn label(&self, text: &str, rect: RECT, font_size: f64, bold: bool, color: u32) {
        self.coverage.bar(rect, 0);
        self.coverage.label(text, rect, font_size, bold, 0xFFFFFF);
        self.paint(rect, color);
    }
    unsafe fn label_right(&self, text: &str, rect: RECT, font_size: f64, bold: bool, color: u32) {
        self.coverage.bar(rect, 0);
        self.coverage.text(
            text,
            rect,
            font_size,
            bold,
            0xFFFFFF,
            DT_SINGLELINE | DT_VCENTER | DT_RIGHT | DT_END_ELLIPSIS | DT_NOPREFIX,
        );
        self.paint(rect, color);
    }
    unsafe fn paragraph(&self, text: &str, rect: RECT, font_size: f64, bold: bool, color: u32) {
        self.coverage.bar(rect, 0);
        self.coverage.text(
            text,
            rect,
            font_size,
            bold,
            0xFFFFFF,
            DT_WORDBREAK | DT_END_ELLIPSIS | DT_NOPREFIX,
        );
        self.paint(rect, color);
    }
    unsafe fn measure(
        &self,
        text: &str,
        width: i32,
        font_size: f64,
        bold: bool,
        flags: u32,
    ) -> (i32, i32) {
        self.coverage.measure(text, width, font_size, bold, flags)
    }
    unsafe fn paint(&self, rect: RECT, color: u32) {
        GdiFlush();
        let rgb = colorref_to_rgb(color);
        let reveal = self.reveal.get();
        let coverage_at = |x: i32, y: i32| {
            let coverage = *self
                .coverage
                .pixels
                .add((y * self.surface.width + x) as usize);
            (coverage & 255)
                .max((coverage >> 8) & 255)
                .max((coverage >> 16) & 255)
        };
        let (offset, shadow) = self.shadow.get();
        if shadow > 0.0 && offset > 0 {
            for y in (rect.top + offset).max(0)..(rect.bottom + offset).min(self.surface.height) {
                for x in rect.left.max(0)..rect.right.min(self.surface.width) {
                    let source = y - offset;
                    if source < rect.top.max(0) || source >= rect.bottom.min(self.surface.height) {
                        continue;
                    }
                    let alpha = (coverage_at(x, source) as f64 * reveal * shadow).round() as u32;
                    if alpha > 0 {
                        let destination = self
                            .surface
                            .pixels
                            .add((y * self.surface.width + x) as usize);
                        *destination = ink_over(*destination, 0, alpha);
                    }
                }
            }
        }
        for y in rect.top.max(0)..rect.bottom.min(self.surface.height) {
            for x in rect.left.max(0)..rect.right.min(self.surface.width) {
                let index = (y * self.surface.width + x) as usize;
                let coverage = *self.coverage.pixels.add(index);
                let alpha = (coverage & 255)
                    .max((coverage >> 8) & 255)
                    .max((coverage >> 16) & 255);
                let alpha = (alpha as f64 * reveal).round() as u32;
                let destination = self.surface.pixels.add(index);
                *destination = ink_over(*destination, rgb, alpha);
            }
        }
    }
    /// Blend analytic coverage, sampled at pixel centres inside a pixel box.
    /// Shapes are resolution independent, so they stay crisp at every DPI.
    unsafe fn shape(
        &self,
        left: f64,
        top: f64,
        right: f64,
        bottom: f64,
        color: u32,
        coverage: impl Fn(f64, f64) -> f64,
    ) {
        let rgb = colorref_to_rgb(color);
        let reveal = self.reveal.get();
        let x0 = (left.floor() as i32).max(0);
        let y0 = (top.floor() as i32).max(0);
        let x1 = (right.ceil() as i32).min(self.surface.width);
        let y1 = (bottom.ceil() as i32).min(self.surface.height);
        for y in y0..y1 {
            for x in x0..x1 {
                let amount = coverage(x as f64 + 0.5, y as f64 + 0.5).clamp(0.0, 1.0) * reveal;
                if amount <= 0.0 {
                    continue;
                }
                let destination = self
                    .surface
                    .pixels
                    .add((y * self.surface.width + x) as usize);
                *destination = ink_over(*destination, rgb, (amount * 255.0).round() as u32);
            }
        }
    }
    unsafe fn disc(&self, cx: f64, cy: f64, radius: f64, color: u32, opacity: f64) {
        let pad = radius + 1.0;
        self.shape(cx - pad, cy - pad, cx + pad, cy + pad, color, |x, y| {
            (radius + 0.5 - (x - cx).hypot(y - cy)) * opacity
        });
    }
    /// A soft light pool behind a status dot; quadratic falloff, no hard edge.
    unsafe fn glow(&self, cx: f64, cy: f64, radius: f64, color: u32, strength: f64) {
        self.shape(
            cx - radius,
            cy - radius,
            cx + radius,
            cy + radius,
            color,
            |x, y| {
                let t = (1.0 - (x - cx).hypot(y - cy) / radius).max(0.0);
                t * t * strength
            },
        );
    }
    /// A ring swept clockwise from twelve o'clock; `sweep` is 0..=1.
    unsafe fn ring(
        &self,
        center: (f64, f64),
        radius: f64,
        thickness: f64,
        sweep: f64,
        color: u32,
        opacity: f64,
    ) {
        let (cx, cy) = center;
        let sweep = sweep.clamp(0.0, 1.0);
        if sweep <= 0.0 {
            return;
        }
        let pad = radius + thickness + 1.0;
        let end = sweep * std::f64::consts::TAU;
        self.shape(cx - pad, cy - pad, cx + pad, cy + pad, color, |x, y| {
            let (dx, dy) = (x - cx, y - cy);
            let band = 0.5 - ((dx.hypot(dy) - radius).abs() - thickness / 2.0);
            if band <= 0.0 {
                return 0.0;
            }
            let along = if sweep >= 1.0 {
                1.0
            } else {
                ((end - dx.atan2(-dy).rem_euclid(std::f64::consts::TAU)) * radius + 0.5)
                    .clamp(0.0, 1.0)
            };
            band.min(1.0) * along * opacity
        });
        if sweep < 1.0 && opacity >= 1.0 {
            // Rounded caps, so a short arc reads as a stroke and not a wedge.
            let cap = thickness / 2.0;
            self.disc(cx, cy - radius, cap, color, 1.0);
            self.disc(
                cx + radius * end.sin(),
                cy - radius * end.cos(),
                cap,
                color,
                1.0,
            );
        }
    }
    unsafe fn segment(&self, from: (f64, f64), to: (f64, f64), thickness: f64, color: u32) {
        let pad = thickness + 1.0;
        let (vx, vy) = (to.0 - from.0, to.1 - from.1);
        let length = (vx * vx + vy * vy).max(f64::EPSILON);
        self.shape(
            from.0.min(to.0) - pad,
            from.1.min(to.1) - pad,
            from.0.max(to.0) + pad,
            from.1.max(to.1) + pad,
            color,
            |x, y| {
                let t = (((x - from.0) * vx + (y - from.1) * vy) / length).clamp(0.0, 1.0);
                let d = (x - from.0 - vx * t).hypot(y - from.1 - vy * t);
                thickness / 2.0 + 0.5 - d
            },
        );
    }
    /// A one-pixel rule that fades out at both ends.
    unsafe fn hairline(&self, left: f64, right: f64, y: f64, color: u32, opacity: f64) {
        let fade = ((right - left) / 4.0).clamp(1.0, 28.0);
        self.shape(left, y.floor(), right, y.floor() + 1.0, color, |x, _| {
            ((x - left) / fade).min((right - x) / fade).clamp(0.0, 1.0) * opacity
        });
    }
}

fn ink_over(destination: u32, rgb: u32, alpha: u32) -> u32 {
    let inverse = 255 - alpha;
    let mut result = (alpha + (((destination >> 24) * inverse + 127) / 255)) << 24;
    for shift in [0, 8, 16] {
        result |=
            (((((rgb >> shift) & 255) * alpha + ((destination >> shift) & 255) * inverse + 127)
                / 255)
                .min(255))
                << shift;
    }
    result
}
impl Drop for Surface {
    fn drop(&mut self) {
        unsafe {
            std::slice::from_raw_parts_mut(self.pixels, (self.width * self.height) as usize)
                .fill(0);
            SelectObject(self.dc, self.previous);
            DeleteObject(self.bitmap);
            DeleteDC(self.dc);
        }
    }
}

/// Signed distance from a pixel centre to the rounded rectangle's edge,
/// negative inside.
fn edge_distance(x: i32, y: i32, width: i32, height: i32, radius: f64) -> f64 {
    let dx = ((x as f64 + 0.5) - width as f64 / 2.0).abs() - (width as f64 / 2.0 - radius);
    let dy = ((y as f64 + 0.5) - height as f64 / 2.0).abs() - (height as f64 / 2.0 - radius);
    dx.max(0.0).hypot(dy.max(0.0)) + dx.max(dy).min(0.0) - radius
}

pub fn corner_alpha(x: i32, y: i32, width: i32, height: i32, radius: f64) -> u8 {
    ((0.5 - edge_distance(x, y, width, height, radius)).clamp(0.0, 1.0) * 255.0).round() as u8
}

/// The corner radius for an island `height` pixels tall: a full pill while
/// compact, a softer card once it grows. Drawing and hit testing share it, so
/// the clickable shape is exactly the painted one at every animation frame.
pub fn island_radius(height: i32, scale: f64) -> f64 {
    (22.0 * scale).min(height as f64 / 2.0)
}

/// Linear blend of two 0x00RRGGBB colours.
fn mix(a: u32, b: u32, t: f64) -> u32 {
    let t = t.clamp(0.0, 1.0);
    let mut result = 0;
    for shift in [0, 8, 16] {
        let x = ((a >> shift) & 255) as f64;
        let y = ((b >> shift) & 255) as f64;
        result |= ((x + (y - x) * t).round().clamp(0.0, 255.0) as u32) << shift;
    }
    result
}

fn luma(color: u32) -> f64 {
    ((color >> 16) & 255) as f64 * 0.2126
        + ((color >> 8) & 255) as f64 * 0.7152
        + (color & 255) as f64 * 0.0722
}

/// Push a colour away from (amount > 1) or towards (< 1) its own grey.
fn saturate(color: u32, amount: f64) -> u32 {
    let grey = luma(color);
    let mut result = 0;
    for shift in [0, 8, 16] {
        let channel = ((color >> shift) & 255) as f64;
        result |= ((grey + (channel - grey) * amount).round().clamp(0.0, 255.0) as u32) << shift;
    }
    result
}

/// Shift a colour's brightness part of the way to `target` luma, keeping its
/// hue. Frosted glass uses it so text keeps its contrast over any wallpaper.
fn toward_luma(color: u32, target: f64, amount: f64) -> u32 {
    let shift_by = (target - luma(color)) * amount;
    let mut result = 0;
    for shift in [0, 8, 16] {
        let channel = ((color >> shift) & 255) as f64;
        result |= ((channel + shift_by).round().clamp(0.0, 255.0) as u32) << shift;
    }
    result
}

/// Lay premultiplied ink *under* existing ink: text drawn earlier stays on top.
fn ink_beneath(ink: u32, rgb: u32, alpha: f64) -> u32 {
    let ink_alpha = (ink >> 24) as f64 / 255.0;
    let weight = alpha.clamp(0.0, 1.0) * (1.0 - ink_alpha);
    let mut result = (((ink_alpha + weight) * 255.0).round().min(255.0) as u32) << 24;
    for shift in [0, 8, 16] {
        let value = ((ink >> shift) & 255) as f64 + ((rgb >> shift) & 255) as f64 * weight;
        result |= (value.round().min(255.0) as u32) << shift;
    }
    result
}

/// A stable per-pixel hash in -2..=2: frosted grain that never shimmers.
fn grain(x: i32, y: i32) -> i32 {
    let mut h = (x as u32).wrapping_mul(0x9E37_79B1) ^ (y as u32).wrapping_mul(0x85EB_CA77);
    h ^= h >> 15;
    h = h.wrapping_mul(0x2C1B_3C6D);
    h ^= h >> 12;
    (h % 5) as i32 - 2
}

/// Text is composited independently of background opacity. It remains readable
/// even when the background is translucent. All returned channels are premultiplied.
pub fn compose_pixel(foreground: u32, background: u32, opacity: f64, mask: u8) -> u32 {
    let foreground_alpha = (foreground >> 24) as f64 / 255.0;
    let background_weight = opacity * (1.0 - foreground_alpha);
    let mask = mask as f64 / 255.0;
    let alpha = ((foreground_alpha + background_weight) * 255.0 * mask)
        .round()
        .clamp(0.0, 255.0) as u32;
    let mut result = alpha << 24;
    for shift in [0, 8, 16] {
        let value = (((foreground >> shift) & 255) as f64
            + ((background >> shift) & 255) as f64 * background_weight)
            * mask;
        result |= (value.round().clamp(0.0, alpha as f64) as u32) << shift;
    }
    result
}

/// The status colour for an activity state, or the ambient blue without one.
/// Mirrored by `IslandPreview.vue`; keep the two tables identical.
fn accent_color(light: bool, state: Option<ActivityState>) -> u32 {
    match (light, state) {
        (true, Some(ActivityState::Waiting)) => 0x001E6492,
        (true, Some(ActivityState::Failed)) => 0x003839BB,
        (true, Some(ActivityState::Success)) => 0x00426D1C,
        (true, _) => 0x00A96522,
        (false, Some(ActivityState::Waiting)) => 0x0089BBF9,
        (false, Some(ActivityState::Failed)) => 0x008F84F7,
        (false, Some(ActivityState::Success)) => 0x00ABDD71,
        _ => 0x00F0B486,
    }
}

/// Smoothstep from the transition's eased progress to content opacity: the
/// body stays out of the way for the first third of the morph, then settles.
fn content_opacity(progress: f64) -> f64 {
    let t = ((progress - 0.3) / 0.7).clamp(0.0, 1.0);
    t * t * (3.0 - 2.0 * t)
}

/// Paint the island into an off-screen premultiplied surface.
///
/// Reads the cached frosted backdrop but never touches a window, so every
/// state can be rendered and inspected in a test. All coordinates below are
/// logical pixels, the same ones `IslandPreview.vue` uses for its CSS.
unsafe fn compose(
    renderer: &Renderer,
    material: SurfaceStyle,
    bounds: Bounds,
    scale: f64,
    model: &IslandModel,
    settings: &Settings,
    progress: f64,
) -> Result<Foreground, String> {
    let surface = Foreground::new(bounds.width, bounds.height)?;
    let light = settings.background_color.is_light();
    let primary = if light { 0x00302014 } else { 0x00F5F1EF };
    let jelly = material == SurfaceStyle::Jelly;
    // Grey reads as dirt on a vivid gel; jelly's second line is a softened
    // primary instead, lifted off the gel by a one-pixel shadow.
    let secondary = match (light, jelly) {
        (true, true) => 0x004A3826,
        (false, true) => 0x00E4DEDA,
        (true, false) => 0x00665442,
        (false, false) => 0x00BFB4AA,
    };
    if jelly && !light {
        surface.shadow.set(((scale.round() as i32).max(1), 0.32));
    }
    let caption = if light { secondary } else { 0x00E2D5CC };
    let unit = |value: f64| (value * scale).round() as i32;
    let at = |value: f64| value * scale;
    let rect = |x: f64, y: f64, w: f64, h: f64| RECT {
        left: unit(x),
        top: unit(y),
        right: unit(x + w),
        bottom: unit(y + h),
    };
    let width = bounds.width as f64 / scale;
    let height = bounds.height as f64 / scale;
    let single = DT_SINGLELINE;
    let focus = model.focus();
    // On jelly the idle clock face takes the text colour: the ambient blue
    // vanishes into a saturated gel. Task states keep their own colours.
    let accent = if jelly && focus.is_none() {
        primary
    } else {
        accent_color(light, focus.map(|a| a.state))
    };
    let ambient = model.ambient();
    let header = model.account_header();
    let header_offset = if header.is_some() { 24.0 } else { 0.0 };
    let clock = ambient.and_then(|content| content.clock.as_deref());
    let clock_width = clock
        .map(|text| {
            surface
                .measure(text, unit(width), 12.0 * scale, true, single)
                .0 as f64
                / scale
                + 1.0
        })
        .unwrap_or(0.0);

    // Status orb: a live dot with a light pool while something is working or
    // waiting, a progress ring around it when the task reports one, and a
    // drawn clock face when the island is only showing the user's own text.
    let (cx, cy) = (at(21.0), at(16.0));
    match focus {
        Some(activity) => {
            if matches!(
                activity.state,
                ActivityState::Running | ActivityState::Waiting
            ) {
                surface.glow(cx, cy, at(10.0), accent, 0.34);
            }
            match activity.progress.as_ref().and_then(|p| p.fraction()) {
                Some(fraction) => {
                    surface.ring((cx, cy), at(6.4), at(1.7), 1.0, accent, 0.26);
                    surface.ring((cx, cy), at(6.4), at(1.7), fraction, accent, 1.0);
                    surface.disc(cx, cy, at(2.4), accent, 1.0);
                }
                None => surface.disc(cx, cy, at(3.7), accent, 1.0),
            }
        }
        None => {
            surface.ring((cx, cy), at(5.7), at(1.35), 1.0, accent, 1.0);
            surface.segment((cx, cy), (cx, cy - at(3.0)), at(1.35), accent);
            surface.segment((cx, cy), (cx + at(2.2), cy + at(1.1)), at(1.35), accent);
        }
    }
    surface.label(
        &model.compact_label().unwrap_or_default(),
        rect(
            33.0,
            0.0,
            width
                - 47.0
                - if clock.is_some() {
                    clock_width + 8.0
                } else {
                    0.0
                },
            32.0,
        ),
        12.0 * scale,
        true,
        primary,
    );
    if let Some(clock) = clock {
        surface.label_right(
            clock,
            rect(width - 14.0 - clock_width, 0.0, clock_width, 32.0),
            12.0 * scale,
            true,
            primary,
        );
    }
    if let Some(header) = header {
        surface.label(
            header,
            rect(16.0, 32.0, width - 32.0, 22.0),
            10.0 * scale,
            false,
            caption,
        );
    }

    // Everything below the header belongs to the target state only.
    surface.reveal.set(content_opacity(progress));
    if model.state() == IslandState::Peek {
        if let Some(text) = model.peek_detail() {
            surface.label(
                &text,
                rect(33.0, 30.0 + header_offset, width - 49.0, 22.0),
                11.0 * scale,
                false,
                secondary,
            );
        }
    }
    if model.state() == IslandState::Expanded {
        // The card shows only the user's expanded template. Tasks reach it
        // through placeholders ({task}, {tasks}, ...), never as implicit rows.
        surface.hairline(
            at(16.0),
            at(width - 16.0),
            at(40.0 + header_offset),
            primary,
            if light { 0.16 } else { 0.14 },
        );
        if let Some(text) = model.expanded_text() {
            // The first line is the headline; anything after it is body.
            // The block is centred in the card's settled height, so it does
            // not slide while the window is still growing.
            let (lead, rest) = text.split_once('\n').unwrap_or((text, ""));
            let column = unit(width - 44.0);
            let lead_height = (surface
                .measure(lead, column, 20.0 * scale, true, DT_WORDBREAK)
                .1 as f64
                / scale)
                .min(56.0);
            let rest_height = surface
                .measure(rest, column, 13.0 * scale, false, DT_WORDBREAK)
                .1 as f64
                / scale;
            let block = lead_height
                + if rest_height > 0.0 {
                    6.0 + rest_height
                } else {
                    0.0
                };
            let settled = model.state().logical_size().1;
            let region = 41.0 + header_offset;
            let top = (region + (settled - 14.0 - region - block) / 2.0).max(54.0 + header_offset);
            surface.paragraph(
                lead,
                rect(22.0, top, width - 44.0, lead_height.max(1.0)),
                20.0 * scale,
                true,
                primary,
            );
            let body = top + lead_height + 6.0;
            surface.paragraph(
                rest,
                rect(22.0, body, width - 44.0, height.min(settled) - body - 14.0),
                13.0 * scale,
                false,
                secondary,
            );
        }
        // Retired account rows still have a reserved footer for old clients.
        if let Some(account) = model.account() {
            surface.paragraph(
                account,
                rect(17.0, 213.0, width - 34.0, 42.0),
                10.0 * scale,
                false,
                secondary,
            );
        }
    }
    GdiFlush();

    let pixels = std::slice::from_raw_parts_mut(
        surface.surface.pixels,
        (bounds.width * bounds.height) as usize,
    );
    let radius = island_radius(bounds.height, scale);
    let opacity = if material == SurfaceStyle::Solid {
        1.0
    } else {
        settings.background_opacity
    };
    let (w, h) = (bounds.width as f64, bounds.height as f64);
    // Edge light. Dark capsules catch a hairline lit from above, light ones a
    // quiet ink outline; jelly always carries a bright, glossy lip.
    let (rim, rim_top, rim_bottom) = match (material, light) {
        (SurfaceStyle::Jelly, _) => (0xFFFFFF, 0.62, 0.14),
        (_, true) => (0x000000, 0.09, 0.15),
        _ => (0xFFFFFF, 0.24, 0.07),
    };
    let sheen_depth = 24.0 * scale;
    let tint = {
        let c = settings.background_color;
        (c.r.clamp(0, 255) as u32) << 16
            | (c.g.clamp(0, 255) as u32) << 8
            | c.b.clamp(0, 255) as u32
    };
    let tint_luma = luma(tint);
    // Jelly: how deep the gel's colour and density gradient reaches, the
    // glossy cap across its top, and where the refracted light pools.
    let gel_depth = (h.min(w) * 0.5).min(18.0 * scale).max(1.0);
    let gel = saturate(tint, 1.35);
    let gel_lit = mix(gel, 0xFFFFFF, 0.18);
    let gel_deep = mix(gel, 0x000000, 0.32);
    let gel_glow = mix(gel, 0xFFFFFF, 0.6);
    let gloss_inset = radius * 0.55 + 2.0 * scale;
    let gloss_top = 2.0 * scale;
    // A thin cap along the top lip: tall enough to read as gloss, short
    // enough to stay clear of the header's glyphs.
    let gloss_bottom = (h * 0.36).min(12.0 * scale).max(gloss_top + 4.0 * scale);
    let gloss_radius = (gloss_bottom - gloss_top) / 2.0;
    let caustic_at = (h * 0.18).min(14.0 * scale);
    let caustic_width = (h * 0.07).clamp(2.5 * scale, 8.0 * scale);
    // Only the sampled background is blurred. Foreground text remains
    // separate; material tint and deterministic grain are applied last.
    for (index, pixel) in pixels.iter_mut().enumerate() {
        let x = index as i32 % bounds.width;
        let y = index as i32 / bounds.width;
        let distance = edge_distance(x, y, bounds.width, bounds.height, radius);
        let a = ((0.5 - distance).clamp(0.0, 1.0) * 255.0).round() as u8;
        if a == 0 {
            *pixel = 0;
            continue;
        }
        let (px, py) = (x as f64 + 0.5, y as f64 + 0.5);
        let depth = py / h;
        let mut ink = *pixel;
        match material {
            SurfaceStyle::Jelly => {
                // Glossy cap: a soft pill of light under the text.
                let dx = (px - w / 2.0).abs() - (w / 2.0 - gloss_inset - gloss_radius);
                let dy = (py - (gloss_top + gloss_bottom) / 2.0).abs() - 0.0;
                let cap = dx.max(0.0).hypot(dy.max(0.0)) + dx.max(dy).min(0.0) - gloss_radius;
                let cover = (0.5 - cap / (1.2 * scale)).clamp(0.0, 1.0);
                if cover > 0.0 {
                    let fade =
                        (1.0 - (py - gloss_top) / (gloss_bottom - gloss_top)).clamp(0.0, 1.0);
                    ink = ink_beneath(ink, 0xFFFFFF, 0.5 * cover * fade.powf(1.6));
                }
            }
            SurfaceStyle::Frosted => {
                // A diagonal specular sweep and light trapped in the glass edge.
                let sweep = (1.0 - (px / w * 0.55 + depth)).max(0.0);
                let edge_glow = (1.0 + distance / (5.0 * scale)).clamp(0.0, 1.0);
                let light_amount = 0.14 * sweep * sweep + 0.09 * edge_glow * edge_glow;
                if light_amount > 0.0 {
                    ink = ink_beneath(ink, 0xFFFFFF, light_amount);
                }
            }
            _ if !light => {
                let sheen = 0.05 * (1.0 - py / sheen_depth).max(0.0);
                if sheen > 0.0 {
                    ink = ink_over(ink, 0xFFFFFF, (sheen * 255.0).round() as u32);
                }
            }
            _ => {}
        }
        let rim_alpha =
            (1.8 + distance).clamp(0.0, 1.0) * (rim_top + (rim_bottom - rim_top) * depth);
        if rim_alpha > 0.0 {
            ink = ink_over(ink, rim, (rim_alpha * 255.0).round() as u32);
        }
        *pixel = match material {
            SurfaceStyle::Frosted => {
                let backdrop = renderer
                    .pixel(bounds.x + x, bounds.y + y)
                    .map(|color| toward_luma(color, tint_luma, 0.3))
                    .unwrap_or(tint);
                let glass = crate::blur::tint(backdrop, tint, opacity);
                let noise = grain(x, y);
                let mut grained = 0;
                for shift in [0, 8, 16] {
                    let channel = ((glass >> shift) & 255) as i32 + noise;
                    grained |= (channel.clamp(0, 255) as u32) << shift;
                }
                compose_pixel(ink, grained, 1.0, a)
            }
            SurfaceStyle::Jelly => {
                // Thicker gel at the rim: deeper colour, higher density.
                let edge = (1.0 + distance / gel_depth).clamp(0.0, 1.0);
                let mut body = mix(gel_lit, gel_deep, edge.powf(1.7));
                let from_bottom = h - py;
                let across = (1.0 - ((px - w / 2.0) / (w / 2.0)).powi(2)).max(0.0);
                let caustic =
                    (-((from_bottom - caustic_at) / caustic_width).powi(2)).exp() * across * 0.32;
                body = mix(body, gel_glow, caustic);
                let density =
                    (opacity + (0.98 - opacity).max(0.0) * edge.powf(2.4)).clamp(0.0, 1.0);
                compose_pixel(ink, body, density, a)
            }
            _ => {
                let grain = if material == SurfaceStyle::Solid {
                    0
                } else {
                    ((x.wrapping_mul(17) ^ y.wrapping_mul(31)) & 3) - 1
                };
                let gradient = (5.0 * (1.0 - depth)).round() as i32 + grain;
                let r = (settings.background_color.r + gradient).clamp(0, 255) as u32;
                let g = (settings.background_color.g + gradient).clamp(0, 255) as u32;
                let b = (settings.background_color.b + gradient).clamp(0, 255) as u32;
                compose_pixel(ink, r << 16 | g << 8 | b, opacity, a)
            }
        };
    }
    Ok(surface)
}

/// Draw and present one frame. `progress` is the running size transition's
/// eased progress (1.0 when settled); it fades in the target state's body.
pub fn draw(
    renderer: &mut Renderer,
    window: &crate::win32::IslandWindow,
    bounds: Bounds,
    scale: f64,
    model: &IslandModel,
    settings: &Settings,
    progress: f64,
) -> Result<bool, String> {
    if bounds.is_empty() {
        return Ok(false);
    }
    unsafe {
        if window.material() == SurfaceStyle::Frosted {
            if let Err(error) = renderer.background(bounds, scale) {
                window.fallback_to_translucent(&error);
                renderer.backdrop = None;
            }
        } else {
            renderer.backdrop = None;
        }
        // Read after sampling: a failed capture has just downgraded it.
        let material = window.material();
        let foreground = compose(renderer, material, bounds, scale, model, settings, progress)?;
        let surface = &foreground.surface;
        let pixels =
            std::slice::from_raw_parts(surface.pixels, (bounds.width * bounds.height) as usize);
        if renderer.last_bounds == Some(bounds) && renderer.last_pixels == pixels {
            return Ok(false);
        }
        let source = POINT { x: 0, y: 0 };
        let position = POINT {
            x: bounds.x,
            y: bounds.y,
        };
        let size = SIZE {
            cx: bounds.width,
            cy: bounds.height,
        };
        let blend = BLENDFUNCTION {
            BlendOp: AC_SRC_OVER as u8,
            BlendFlags: 0,
            SourceConstantAlpha: 255,
            AlphaFormat: AC_SRC_ALPHA as u8,
        };
        if UpdateLayeredWindow(
            window.handle(),
            std::ptr::null_mut(),
            &position,
            &size,
            surface.dc,
            &source,
            0,
            &blend,
            ULW_ALPHA,
        ) == 0
        {
            return Err("could not present island surface".into());
        }
        renderer.last_pixels.fill(0);
        renderer.last_pixels = pixels.to_vec();
        renderer.last_bounds = Some(bounds);
    }
    Ok(true)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn windows_text_is_terminated_and_counts_utf16_not_utf8_bytes() {
        assert_eq!(windows_text_buffer(""), vec![0]);
        let text = windows_text_buffer("中文🕒");
        assert_eq!(text.last(), Some(&0));
        assert_eq!(text.len() - 1, 4);
        assert_eq!(
            String::from_utf16(&text[..text.len() - 1]).unwrap(),
            "中文🕒"
        );
    }

    #[test]
    fn empty_and_clipped_text_never_enters_native_drawing() {
        unsafe {
            let surface = Surface::new(32, 32).unwrap();
            let pixels = std::slice::from_raw_parts_mut(surface.pixels, 32 * 32);
            pixels.fill(0);
            let rect = RECT {
                left: 0,
                top: 0,
                right: 32,
                bottom: 32,
            };
            surface.label("", rect, 12.0, false, 0xFFFFFF);
            surface.paragraph("", rect, 12.0, 0xFFFFFF);
            surface.label("clipped", RECT { right: 0, ..rect }, 12.0, false, 0xFFFFFF);
            surface.label("clipped", RECT { bottom: 0, ..rect }, 12.0, false, 0xFFFFFF);
            surface.label("invalid", rect, f64::NAN, false, 0xFFFFFF);
            GdiFlush();
            assert!(pixels.iter().all(|&p| p == 0));
        }
    }
    /// Explicit desktop integration check. The backdrop covers the complete
    /// sampling rectangle with our own pixels; no other app is captured/exported.
    #[test]
    #[ignore = "requires an unlocked interactive Windows desktop"]
    fn native_glass_blurs_background_not_text_and_does_not_sample_itself() {
        use windows_sys::Win32::UI::WindowsAndMessaging::*;
        struct OwnedBackdrop(windows_sys::Win32::Foundation::HWND);
        impl Drop for OwnedBackdrop {
            fn drop(&mut self) {
                unsafe {
                    DestroyWindow(self.0);
                }
            }
        }
        crate::win32::enable_dpi_awareness();
        let monitor = crate::win32::primary_monitor().unwrap();
        let bounds = Bounds {
            x: monitor.x + 100,
            y: monitor.y + 100,
            width: 340,
            height: 60,
        };
        let pad = 96;
        let backdrop_width = 510 + 2 * pad;
        let backdrop_height = 390 + 2 * pad;
        let window = crate::win32::IslandWindow::create(SurfaceStyle::Frosted).unwrap();
        assert_eq!(
            window.material(),
            SurfaceStyle::Frosted,
            "{:?}",
            window.material_fallback()
        );
        unsafe {
            let backdrop = OwnedBackdrop(CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                "STATIC\0".encode_utf16().collect::<Vec<_>>().as_ptr(),
                std::ptr::null(),
                WS_POPUP,
                bounds.x - pad,
                bounds.y - pad,
                backdrop_width,
                backdrop_height,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                std::ptr::null(),
            ));
            assert!(!backdrop.0.is_null());
            ShowWindow(backdrop.0, SW_SHOWNOACTIVATE);
            let dc = GetDC(backdrop.0);
            for x in (0..backdrop_width).step_by(6) {
                let color = if x % 12 == 0 {
                    if x < pad + bounds.width / 2 {
                        0xB878EA
                    } else {
                        0xD7C450
                    }
                } else {
                    0x302820
                };
                let brush = CreateSolidBrush(color);
                FillRect(
                    dc,
                    &RECT {
                        left: x,
                        top: 0,
                        right: x + 6,
                        bottom: backdrop_height,
                    },
                    brush,
                );
                DeleteObject(brush);
            }
            ReleaseDC(backdrop.0, dc);
            GdiFlush();
            let mut model = IslandModel::new();
            model.set_ambient(Some(crate::ambient::AmbientContent {
                clock: Some("12:34".into()),
                date: "2026-10-06".into(),
                text: "Qing Island".into(),
                ..Default::default()
            }));
            model.set_hovered(true);
            let settings = Settings {
                surface_style: SurfaceStyle::Frosted,
                background_opacity: 0.5,
                ..Default::default()
            };
            let mut renderer = Renderer::default();
            window.apply(bounds, true, 1.0).unwrap();
            std::thread::sleep(Duration::from_millis(80));
            assert!(draw(&mut renderer, &window, bounds, 1.0, &model, &settings, 1.0).unwrap());
            window.set_visible(true);
            std::thread::sleep(Duration::from_millis(80));
            renderer.backdrop.as_mut().unwrap().sampled = Instant::now() - BACKGROUND_INTERVAL;
            // A second capture with our card now visible must still see only
            // the stripes; otherwise it recursively blurs its own text.
            assert!(
                !draw(&mut renderer, &window, bounds, 1.0, &model, &settings, 1.0).unwrap(),
                "background capture included the island itself or was unstable"
            );
            assert!(renderer.samples >= 2);
            let at = |x: usize, y: usize| renderer.last_pixels[y * bounds.width as usize + x];
            let left = at(70, 55);
            let adjacent = at(76, 55);
            let right = at(270, 55);
            for shift in [0, 8, 16] {
                let difference =
                    (((left >> shift) & 255) as i32 - ((adjacent >> shift) & 255) as i32).abs();
                assert!(
                    difference <= 8,
                    "6px stripes were not blurred: {difference}"
                );
            }
            assert!(
                ((left >> 16) & 255) > ((right >> 16) & 255) + 15,
                "left/right background lost its color"
            );
            assert!(
                ((right >> 8) & 255) > ((left >> 8) & 255) + 8,
                "glass became a flat tint"
            );
            assert!(
                renderer
                    .last_pixels
                    .iter()
                    .any(|&p| (p & 255) > 230 && ((p >> 8) & 255) > 230),
                "foreground text was blurred away"
            );
            eprintln!(
                "Native glass verified: {} samples; latest {} us",
                renderer.samples, renderer.sample_micros
            );
            if let Some(path) = std::env::var_os("QING_ISLAND_TEST_IMAGE") {
                // Test-only BMP of our composed card, never a runtime export API.
                let pixels = &renderer.last_pixels;
                let bytes = 54 + pixels.len() * 4;
                let mut bmp = Vec::with_capacity(bytes);
                bmp.extend_from_slice(b"BM");
                bmp.extend_from_slice(&(bytes as u32).to_le_bytes());
                bmp.extend_from_slice(&[0; 4]);
                bmp.extend_from_slice(&54u32.to_le_bytes());
                bmp.extend_from_slice(&40u32.to_le_bytes());
                bmp.extend_from_slice(&bounds.width.to_le_bytes());
                bmp.extend_from_slice(&(-bounds.height).to_le_bytes());
                bmp.extend_from_slice(&1u16.to_le_bytes());
                bmp.extend_from_slice(&32u16.to_le_bytes());
                bmp.extend_from_slice(&[0; 24]);
                for pixel in pixels {
                    bmp.extend_from_slice(&pixel.to_le_bytes());
                }
                std::fs::write(path, &bmp).unwrap();
                bmp.fill(0);
            }
            renderer.clear();
            assert!(renderer.backdrop.is_none() && renderer.last_pixels.is_empty());
            window.set_visible(false);
            // The user-visible regression: a clock-only island has an empty
            // custom-text paragraph on expansion. Exercise real GDI drawing,
            // not only the model, including transient resize clipping.
            model.set_ambient(Some(crate::ambient::AmbientContent {
                clock: Some("12:34".into()),
                date: String::new(),
                text: String::new(),
                ..Default::default()
            }));
            model.toggle_expanded();
            assert_eq!(model.state(), IslandState::Expanded);
            for style in [
                SurfaceStyle::Solid,
                SurfaceStyle::Translucent,
                SurfaceStyle::Frosted,
                SurfaceStyle::Jelly,
            ] {
                let expanded_window = crate::win32::IslandWindow::create(style).unwrap();
                let settings = Settings {
                    surface_style: style,
                    ..settings.clone()
                };
                for scale in [0.75, 1.0, 1.5] {
                    for height in [32, 60, 260] {
                        let expanded = Bounds {
                            width: (340.0 * scale) as i32,
                            height: (height as f64 * scale) as i32,
                            ..bounds
                        };
                        expanded_window.apply(expanded, true, scale).unwrap();
                        assert!(draw(
                            &mut renderer,
                            &expanded_window,
                            expanded,
                            scale,
                            &model,
                            &settings,
                            1.0
                        )
                        .unwrap());
                    }
                }
                renderer.clear();
            }
            eprintln!("Clock-only expanded native drawing verified: 4 materials, 3 scales, 3 resize heights.");
        }
    }
    fn gallery_model(
        state: IslandState,
        focus: Option<crate::activity::LiveActivity>,
    ) -> IslandModel {
        use crate::activity::{ActivityProgress, ActivityState, LiveActivity, ProviderKind};
        let mut model = IslandModel::new();
        model.set_ambient(Some(crate::ambient::AmbientContent {
            clock: Some("12:34".into()),
            date: "2026-10-09 · 周五".into(),
            text: "专注当下 · Focus".into(),
            peek_text: Some("2026-10-09 · 周五 · 剩余 68%".into()),
            expanded_text: Some(
                "周五 · 专注当下\n2026-10-09 12:34\n剩余额度 68%，2 小时 14 分后重置".into(),
            ),
        }));
        if let Some(focus) = focus {
            let stack = vec![
                focus.clone(),
                LiveActivity::running("w", ProviderKind::Mock, "demo", "Mock approval 2")
                    .with_subtitle("Waiting for you")
                    .with_state(ActivityState::Waiting),
                LiveActivity::running("s", ProviderKind::Mock, "demo", "Mock completion 3")
                    .with_subtitle("Finished")
                    .with_state(ActivityState::Success)
                    .with_progress(ActivityProgress::determinate(20.0, 20.0)),
            ];
            model.set_content(Some(focus), stack, 1, None);
        }
        match state {
            IslandState::Peek => model.set_hovered(true),
            IslandState::Expanded => model.toggle_expanded(),
            _ => {}
        }
        assert_eq!(model.state(), state);
        model
    }

    fn compose_pixels(
        model: &IslandModel,
        settings: &Settings,
        material: SurfaceStyle,
        scale: f64,
        progress: f64,
        name: &str,
    ) -> (Bounds, Vec<u32>) {
        let (w, h) = model.state().logical_size();
        let bounds = Bounds {
            x: 0,
            y: 0,
            width: (w * scale).round() as i32,
            height: (h * scale).round() as i32,
        };
        let mut renderer = Renderer::default();
        if material == SurfaceStyle::Frosted {
            // A synthetic desktop under the island: wide colour stripes.
            let pad = 40;
            let region = Bounds {
                x: -pad,
                y: -pad,
                width: bounds.width + pad * 2,
                height: bounds.height + pad * 2,
            };
            let (sw, sh) = (
                ((region.width + 1) / 2) as usize,
                ((region.height + 1) / 2) as usize,
            );
            let raw = (0..sw * sh)
                .map(|i| match (i % sw) / 6 % 3 {
                    0 => 0xEA78B8,
                    1 => 0x50C4D7,
                    _ => 0xF6C445,
                })
                .collect::<Vec<u32>>();
            let blurred = frost(&raw, sw, sh, scale).unwrap();
            renderer.backdrop = Some(Backdrop {
                region,
                output: bounds,
                width: sw,
                height: sh,
                raw,
                blurred,
                sampled: Instant::now(),
            });
        }
        let pixels = unsafe {
            let surface = compose(
                &renderer, material, bounds, scale, model, settings, progress,
            )
            .unwrap();
            std::slice::from_raw_parts(
                surface.surface.pixels,
                (bounds.width * bounds.height) as usize,
            )
            .to_vec()
        };
        if let Some(folder) = std::env::var_os("QING_ISLAND_GALLERY") {
            // Test-only review images of our own composed card.
            let path = std::path::Path::new(&folder).join(format!("{name}.bmp"));
            let bytes = 54 + pixels.len() * 4;
            let mut bmp = Vec::with_capacity(bytes);
            bmp.extend_from_slice(b"BM");
            bmp.extend_from_slice(&(bytes as u32).to_le_bytes());
            bmp.extend_from_slice(&[0; 4]);
            bmp.extend_from_slice(&54u32.to_le_bytes());
            bmp.extend_from_slice(&40u32.to_le_bytes());
            bmp.extend_from_slice(&bounds.width.to_le_bytes());
            bmp.extend_from_slice(&(-bounds.height).to_le_bytes());
            bmp.extend_from_slice(&1u16.to_le_bytes());
            bmp.extend_from_slice(&32u16.to_le_bytes());
            bmp.extend_from_slice(&[0; 24]);
            for pixel in &pixels {
                bmp.extend_from_slice(&pixel.to_le_bytes());
            }
            std::fs::write(path, &bmp).unwrap();
        }
        (bounds, pixels)
    }

    #[test]
    fn every_state_composes_off_screen_with_orb_rim_and_faded_body() {
        use crate::activity::{ActivityProgress, LiveActivity, ProviderKind};
        let dark = Settings::default();
        let light = Settings {
            background_color: crate::settings::RgbColor {
                r: 240,
                g: 232,
                b: 215,
            },
            ..Settings::default()
        };
        let task = LiveActivity::running("p", ProviderKind::Mock, "demo", "Mock export")
            .with_subtitle("Compressing assets")
            .with_progress(ActivityProgress::determinate(5.0, 20.0));
        let channel = |p: u32, shift: u32| (p >> shift) & 255;
        let lum = |p: u32| channel(p, 16) + channel(p, 8) + channel(p, 0);
        for (tag, settings) in [("dark", &dark), ("light", &light)] {
            for (state, focus) in [
                (IslandState::Compact, None),
                (IslandState::Compact, Some(task.clone())),
                (IslandState::Peek, None),
                (IslandState::Expanded, None),
                (IslandState::Expanded, Some(task.clone())),
            ] {
                let model = gallery_model(state, focus.clone());
                for scale in [1.0, 1.5] {
                    let name = format!(
                        "{tag}-{}-{}-{}",
                        state.as_str(),
                        if focus.is_some() { "task" } else { "ambient" },
                        (scale * 100.0) as i32
                    );
                    let (bounds, pixels) =
                        compose_pixels(&model, settings, SurfaceStyle::Solid, scale, 1.0, &name);
                    let at = |x: i32, y: i32| pixels[(y * bounds.width + x) as usize];
                    // Rounded corners are fully transparent; the body is opaque.
                    assert_eq!(at(0, 0), 0);
                    assert_eq!(at(bounds.width / 2, bounds.height - 3) >> 24, 255);
                    // The rim separates the edge from the body.
                    let edge = at(bounds.width - 1, bounds.height / 2);
                    let inner = at(bounds.width - 4, bounds.height / 2);
                    if tag == "dark" {
                        assert!(lum(edge) > lum(inner) + 20, "{name}: no lit rim");
                    } else {
                        assert!(lum(edge) + 20 < lum(inner), "{name}: no ink rim");
                    }
                }
            }
            // Progress ring: the swept quarter is accent, the rest only a track.
            let model = gallery_model(IslandState::Compact, Some(task.clone()));
            let (bounds, pixels) = compose_pixels(
                &model,
                settings,
                SurfaceStyle::Solid,
                2.0,
                1.0,
                &format!("{tag}-ring-200"),
            );
            let at = |x: f64, y: f64| {
                pixels[((y * 2.0) as i32 * bounds.width + (x * 2.0) as i32) as usize]
            };
            let swept = at(21.0 + 4.5, 16.0 - 4.5);
            let unswept = at(21.0 - 4.5, 16.0 + 4.5);
            let tint = |p: u32| (channel(p, 0) as i32 - channel(p, 16) as i32).abs();
            assert!(
                tint(swept) > tint(unswept) + 25,
                "{tag}: progress ring not swept ({swept:08x} vs {unswept:08x})"
            );
        }
        // The expanded body fades with the morph; the header never does.
        let model = gallery_model(IslandState::Expanded, None);
        let (_, start) = compose_pixels(
            &model,
            &dark,
            SurfaceStyle::Solid,
            1.0,
            0.0,
            "dark-morph-start",
        );
        let (bounds, end) = compose_pixels(
            &model,
            &dark,
            SurfaceStyle::Solid,
            1.0,
            1.0,
            "dark-morph-end",
        );
        let count = |pixels: &[u32], rows: std::ops::Range<i32>, columns: std::ops::Range<i32>| {
            rows.flat_map(|y| {
                columns
                    .clone()
                    .map(move |x| (y * bounds.width + x) as usize)
            })
            .filter(|&i| channel(pixels[i], 8) > 160)
            .count()
        };
        assert_eq!(
            count(&start, 56..120, 22..300),
            0,
            "body visible before the morph"
        );
        assert!(
            count(&end, 56..120, 22..300) > 40,
            "expanded body text missing"
        );
        assert_eq!(
            count(&start, 6..26, 34..140),
            count(&end, 6..26, 34..140),
            "the header must not fade during a morph"
        );
        assert!(count(&end, 6..26, 34..140) > 20, "header text missing");
    }

    #[test]
    fn frosted_glass_is_blurred_vivid_and_keeps_text_crisp() {
        let model = gallery_model(IslandState::Peek, None);
        let settings = Settings {
            surface_style: SurfaceStyle::Frosted,
            background_opacity: 0.45,
            ..Settings::default()
        };
        let (bounds, pixels) = compose_pixels(
            &model,
            &settings,
            SurfaceStyle::Frosted,
            1.5,
            1.0,
            "frosted-peek-150",
        );
        let at = |x: i32, y: i32| pixels[(y * bounds.width + x) as usize];
        let channel = |p: u32, shift: u32| ((p >> shift) & 255) as i32;
        // 12 px stripes (6 px at half resolution) are gone, not merely softened.
        let row = bounds.height - 10;
        let (a, b) = (at(260, row), at(272, row));
        for shift in [0, 8, 16] {
            assert!(
                (channel(a, shift) - channel(b, shift)).abs() <= 14,
                "{a:08x} vs {b:08x}"
            );
        }
        // ...yet the glass is not a flat tint: the desktop's colour survives.
        let spread = (0..bounds.width)
            .map(|x| channel(at(x, row), 16) - channel(at(x, row), 0))
            .fold((i32::MAX, i32::MIN), |(lo, hi), v| (lo.min(v), hi.max(v)));
        assert!(spread.1 - spread.0 > 6, "frost lost all colour: {spread:?}");
        assert!(
            pixels
                .iter()
                .any(|&p| channel(p, 0) > 225 && channel(p, 8) > 225 && (p >> 24) == 255),
            "text must stay crisp on top of the frost"
        );
        for &p in &pixels {
            for shift in [0, 8, 16] {
                assert!(((p >> shift) & 255) <= p >> 24, "not premultiplied");
            }
        }
    }

    #[test]
    fn jelly_is_denser_at_its_rim_and_glossy_on_top() {
        let model = gallery_model(IslandState::Peek, None);
        for (tag, color) in [
            (
                "cherry",
                crate::settings::RgbColor {
                    r: 220,
                    g: 38,
                    b: 82,
                },
            ),
            (
                "lime",
                crate::settings::RgbColor {
                    r: 120,
                    g: 200,
                    b: 60,
                },
            ),
            ("ink", crate::settings::RgbColor::default()),
        ] {
            let settings = Settings {
                surface_style: SurfaceStyle::Jelly,
                background_opacity: 0.6,
                background_color: color,
                ..Settings::default()
            };
            let (bounds, pixels) = compose_pixels(
                &model,
                &settings,
                SurfaceStyle::Jelly,
                1.5,
                1.0,
                &format!("jelly-{tag}-peek-150"),
            );
            let at = |x: i32, y: i32| pixels[(y * bounds.width + x) as usize];
            let alpha = |p: u32| (p >> 24) as i32;
            let rim = at(bounds.width - 2, bounds.height / 2);
            let inside = at(bounds.width - 45, bounds.height - 22);
            assert!(
                alpha(rim) > alpha(inside) + 30,
                "{tag}: gel must thicken at the rim"
            );
            // The glossy cap lifts the top of the body above its lower half.
            let lum = |p: u32| (p & 255) + ((p >> 8) & 255) + ((p >> 16) & 255);
            let cap = at(bounds.width - 60, 5);
            let below = at(bounds.width - 60, bounds.height / 2 + 8);
            assert!(lum(cap) > lum(below) + 40, "{tag}: no glossy cap");
        }
        let jelly_model = gallery_model(IslandState::Expanded, None);
        let settings = Settings {
            surface_style: SurfaceStyle::Jelly,
            background_color: crate::settings::RgbColor {
                r: 120,
                g: 60,
                b: 220,
            },
            ..Settings::default()
        };
        compose_pixels(
            &jelly_model,
            &settings,
            SurfaceStyle::Jelly,
            1.5,
            1.0,
            "jelly-grape-expanded-150",
        );
        compose_pixels(
            &gallery_model(IslandState::Compact, None),
            &settings,
            SurfaceStyle::Jelly,
            1.5,
            1.0,
            "jelly-grape-compact-150",
        );
    }

    #[test]
    fn hit_testing_and_drawing_share_one_corner_radius() {
        assert_eq!(island_radius(32, 1.0), 16.0);
        assert_eq!(island_radius(60, 1.0), 22.0);
        assert_eq!(island_radius(390, 1.5), 33.0);
        // Mid-morph heights keep the pill shape until the card radius caps it.
        assert_eq!(island_radius(40, 1.0), 20.0);
    }

    #[test]
    fn translucent_background_does_not_make_text_equally_transparent() {
        let background = compose_pixel(0, 0x171920, 0.35, 255);
        let text = compose_pixel(0xFFF5F1EF, 0x171920, 0.35, 255);
        assert!((background >> 24) < 100);
        assert!((text >> 24) > 245);
        assert_eq!(compose_pixel(0xFFFFFFFF, 0, 0.72, 0), 0);
        for value in [background, text] {
            for shift in [0, 8, 16] {
                assert!(((value >> shift) & 255) <= value >> 24);
            }
        }
    }
    #[test]
    fn dark_and_black_ink_are_opaque_on_light_custom_colors() {
        for rgb in [0, 0x142030, 0xEEF1F5] {
            let ink = ink_over(0, rgb, 255);
            assert_eq!(ink >> 24, 255);
            assert_eq!(compose_pixel(ink, 0xFFFFFF, 0.35, 255), 0xFF000000 | rgb);
        }
        assert_eq!(ink_over(0, 0x142030, 0), 0);
        let ink = ink_over(0, 0x142030, 128);
        assert_eq!(ink >> 24, 128);
        for shift in [0, 8, 16] {
            assert!(((ink >> shift) & 255) <= ink >> 24);
        }
    }
    #[test]
    fn rounded_mask_matches_visible_and_click_through_pixels() {
        assert_eq!(corner_alpha(0, 0, 232, 32, 16.0), 0);
        assert_eq!(corner_alpha(115, 15, 232, 32, 16.0), 255);
    }
}
