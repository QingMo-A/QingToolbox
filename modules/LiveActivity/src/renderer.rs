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
        let pad = (18.0 * scale).round().clamp(8.0, 96.0) as i32;
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
            crate::blur::gaussian(
                &raw,
                width as usize,
                height as usize,
                (4.0 * scale).round().clamp(3.0, 16.0) as usize,
            )
            .map_err(str::to_string)?
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
    fn pixel(&self, x: i32, y: i32) -> Option<u32> {
        let b = self.backdrop.as_ref()?;
        let sx = (((x - b.region.x) as i64 * b.width as i64) / b.region.width as i64)
            .clamp(0, b.width as i64 - 1) as usize;
        let sy = (((y - b.region.y) as i64 * b.height as i64) / b.region.height as i64)
            .clamp(0, b.height as i64 - 1) as usize;
        Some(b.blurred[sy * b.width + sx])
    }
}
impl Drop for Renderer {
    fn drop(&mut self) {
        self.clear();
    }
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
        if text.is_empty()
            || rect.right <= rect.left
            || rect.bottom <= rect.top
            || !font_size.is_finite()
            || font_size <= 0.0
        {
            return;
        }
        let face: Vec<u16> = "Segoe UI\0".encode_utf16().collect();
        let font = CreateFontW(
            -(font_size.round() as i32),
            0,
            0,
            0,
            if bold { 700 } else { 400 },
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
            return;
        }
        let old = SelectObject(self.dc, font);
        if old.is_null() || old as isize == GDI_ERROR as isize {
            DeleteObject(font);
            return;
        }
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
        SelectObject(self.dc, old);
        DeleteObject(font);
    }
    unsafe fn bar(&self, rect: RECT, color: u32) {
        let brush = CreateSolidBrush(color);
        FillRect(self.dc, &rect, brush);
        DeleteObject(brush);
    }
}

fn windows_text_buffer(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(std::iter::once(0)).collect()
}

/// GDI's RGB brightness is not alpha: dark glyphs must be as opaque as white
/// glyphs. Render coverage separately, then store premultiplied ARGB ink.
struct Foreground {
    surface: Surface,
    coverage: Surface,
}
impl Foreground {
    unsafe fn new(width: i32, height: i32) -> Result<Self, String> {
        let surface = Surface::new(width, height)?;
        std::slice::from_raw_parts_mut(surface.pixels, (width * height) as usize).fill(0);
        Ok(Self {
            surface,
            coverage: Surface::new(width, height)?,
        })
    }
    unsafe fn label(&self, text: &str, rect: RECT, font_size: f64, bold: bool, color: u32) {
        self.coverage.bar(rect, 0);
        self.coverage.label(text, rect, font_size, bold, 0xFFFFFF);
        self.paint(rect, color);
    }
    unsafe fn paragraph(&self, text: &str, rect: RECT, font_size: f64, color: u32) {
        self.coverage.bar(rect, 0);
        self.coverage.paragraph(text, rect, font_size, 0xFFFFFF);
        self.paint(rect, color);
    }
    unsafe fn bar(&self, rect: RECT, color: u32) {
        self.coverage.bar(rect, 0xFFFFFF);
        self.paint(rect, color);
    }
    unsafe fn paint(&self, rect: RECT, color: u32) {
        GdiFlush();
        // COLORREF is 0x00BBGGRR; the layered DIB uses 0xAARRGGBB.
        let rgb = ((color & 255) << 16) | (color & 0xFF00) | ((color >> 16) & 255);
        for y in rect.top.max(0)..rect.bottom.min(self.surface.height) {
            for x in rect.left.max(0)..rect.right.min(self.surface.width) {
                let index = (y * self.surface.width + x) as usize;
                let coverage = *self.coverage.pixels.add(index);
                let alpha = (coverage & 255)
                    .max((coverage >> 8) & 255)
                    .max((coverage >> 16) & 255);
                let destination = self.surface.pixels.add(index);
                *destination = ink_over(*destination, rgb, alpha);
            }
        }
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

pub fn corner_alpha(x: i32, y: i32, width: i32, height: i32, radius: f64) -> u8 {
    let dx = ((x as f64 + 0.5) - width as f64 / 2.0).abs() - (width as f64 / 2.0 - radius);
    let dy = ((y as f64 + 0.5) - height as f64 / 2.0).abs() - (height as f64 / 2.0 - radius);
    let distance = dx.max(0.0).hypot(dy.max(0.0)) + dx.max(dy).min(0.0) - radius;
    ((0.5 - distance).clamp(0.0, 1.0) * 255.0).round() as u8
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

pub fn draw(
    renderer: &mut Renderer,
    window: &crate::win32::IslandWindow,
    bounds: Bounds,
    scale: f64,
    model: &IslandModel,
    settings: &Settings,
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
        let surface = Foreground::new(bounds.width, bounds.height)?;
        let light = settings.background_color.is_light();
        let primary = if light { 0x00302014 } else { 0x00F5F1EF };
        let secondary = if light { 0x00665442 } else { 0x00BFB4AA };
        let caption = if light { secondary } else { 0x00E2D5CC };
        let unit = |value: f64| (value * scale).round() as i32;
        let rect = |x: f64, y: f64, w: f64, h: f64| RECT {
            left: unit(x),
            top: unit(y),
            right: unit(x + w),
            bottom: unit(y + h),
        };
        let width = bounds.width as f64 / scale;
        let accent = match (light, model.focus().map(|a| a.state)) {
            (true, Some(ActivityState::Waiting)) => 0x001E6492,
            (true, Some(ActivityState::Failed)) => 0x003839BB,
            (true, Some(ActivityState::Success)) => 0x00426D1C,
            (true, _) => 0x00A96522,
            (false, Some(ActivityState::Waiting)) => 0x0089BBF9,
            (false, Some(ActivityState::Failed)) => 0x008F84F7,
            (false, Some(ActivityState::Success)) => 0x00ABDD71,
            _ => 0x00F0B486,
        };
        let ambient = model.ambient();
        let expanded_extra = model.expanded_text_extra_height();
        let header = model.account_header();
        let header_offset = if header.is_some() { 24.0 } else { 0.0 };
        let clock = ambient.and_then(|content| content.clock.as_deref());
        let clock_width = clock
            .map(|text| text.len() as f64 * 7.1 + 5.0)
            .unwrap_or(0.0);
        surface.label(
            if model.focus().is_some() {
                "●"
            } else {
                "◷"
            },
            rect(13.0, 0.0, 16.0, 32.0),
            11.0 * scale,
            false,
            accent,
        );
        surface.label(
            &model.compact_label().unwrap_or_default(),
            rect(34.0, 0.0, width - 48.0 - clock_width, 32.0),
            12.0 * scale,
            true,
            primary,
        );
        if let Some(clock) = clock {
            surface.label(
                clock,
                rect(width - clock_width - 12.0, 0.0, clock_width, 32.0),
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
        if model.state() != IslandState::Compact {
            surface.label(
                &model.peek_detail().unwrap_or_else(|| "点击查看活动".into()),
                rect(16.0, 31.0 + header_offset, width - 32.0, 25.0),
                11.0 * scale,
                false,
                secondary,
            );
        }
        if model.state() == IslandState::Expanded && model.stack().is_empty() {
            if let Some(content) = ambient {
                if let Some(text) = content.expanded_text.as_deref() {
                    surface.paragraph(
                        text,
                        rect(
                            22.0,
                            66.0 + header_offset,
                            width - 44.0,
                            140.0 - header_offset,
                        ),
                        16.0 * scale,
                        primary,
                    );
                } else if let Some(clock) = content.clock.as_deref() {
                    surface.label(
                        &content.date,
                        rect(22.0, 66.0 + header_offset, width - 44.0, 25.0),
                        12.0 * scale,
                        false,
                        secondary,
                    );
                    surface.label(
                        clock,
                        rect(
                            22.0,
                            99.0 + if header.is_some() { 19.0 } else { 0.0 },
                            width - 44.0,
                            52.0,
                        ),
                        32.0 * scale,
                        true,
                        primary,
                    );
                    surface.paragraph(
                        &content.text,
                        rect(
                            22.0,
                            if header.is_some() { 180.0 } else { 173.0 },
                            width - 44.0,
                            if model.account().is_some() {
                                if header.is_some() {
                                    26.0
                                } else {
                                    33.0
                                }
                            } else {
                                62.0
                            },
                        ),
                        14.0 * scale,
                        caption,
                    );
                } else {
                    surface.paragraph(
                        &content.text,
                        rect(
                            22.0,
                            83.0 + header_offset,
                            width - 44.0,
                            if model.account().is_some() {
                                120.0 - header_offset
                            } else {
                                138.0
                            },
                        ),
                        19.0 * scale,
                        primary,
                    );
                }
            }
        } else if model.state() == IslandState::Expanded {
            for (index, activity) in model.stack().iter().take(3).enumerate() {
                let y = if header.is_some() {
                    84.0 + index as f64 * 42.0
                } else {
                    66.0 + index as f64 * 49.0
                };
                surface.label(
                    &activity.title,
                    rect(17.0, y, width - 34.0, 20.0),
                    12.0 * scale,
                    true,
                    primary,
                );
                surface.label(
                    activity
                        .subtitle
                        .as_deref()
                        .unwrap_or(activity.state.as_str()),
                    rect(17.0, y + 20.0, width - 34.0, 17.0),
                    10.0 * scale,
                    false,
                    secondary,
                );
                if let Some(fraction) = activity.progress.as_ref().and_then(|p| p.fraction()) {
                    surface.bar(
                        rect(17.0, y + 40.0, width - 34.0, 2.0),
                        if light { 0x00CFC3B8 } else { 0x004B4036 },
                    );
                    surface.bar(rect(17.0, y + 40.0, (width - 34.0) * fraction, 2.0), accent);
                }
            }
            let footer = model
                .account()
                .map(str::to_string)
                .or_else(|| {
                    ambient
                        .filter(|c| !c.text.is_empty())
                        .map(|c| c.text.clone())
                })
                .unwrap_or_else(|| {
                    if model.overflow() > 0 {
                        format!("另有 {} 项活动 · 点击收起", model.overflow())
                    } else {
                        "点击收起 · 移开鼠标自动收起".into()
                    }
                });
            if let Some(text) = model.expanded_text() {
                surface.paragraph(
                    text,
                    rect(17.0, 213.0, width - 34.0, expanded_extra),
                    12.0 * scale,
                    secondary,
                );
            } else if model.account().is_none() {
                surface.label(
                    &footer,
                    rect(17.0, 229.0, width - 34.0, 23.0),
                    10.0 * scale,
                    false,
                    secondary,
                );
            }
        }
        // Account quota is independent of task activity. A clock-only island
        // must expose it too; reserve two footer lines for remaining/reset.
        if model.state() == IslandState::Expanded {
            if let Some(account) = model.account() {
                surface.paragraph(
                    account,
                    rect(17.0, 213.0 + expanded_extra, width - 34.0, 42.0),
                    10.0 * scale,
                    secondary,
                );
            }
        }
        GdiFlush();
        let surface = &surface.surface;
        let pixels =
            std::slice::from_raw_parts_mut(surface.pixels, (bounds.width * bounds.height) as usize);
        let radius = ((if model.state() == IslandState::Compact {
            16.0
        } else {
            18.0
        }) * scale)
            .min(bounds.height as f64 / 2.0);
        let opacity = if window.material() == SurfaceStyle::Solid {
            1.0
        } else {
            settings.background_opacity
        };
        // Only the sampled background is blurred. Foreground text remains
        // separate; material tint and deterministic grain are applied last.
        for (index, pixel) in pixels.iter_mut().enumerate() {
            let x = index as i32 % bounds.width;
            let y = index as i32 / bounds.width;
            let a = corner_alpha(x, y, bounds.width, bounds.height, radius);
            let grain = if window.material() == SurfaceStyle::Solid {
                0
            } else {
                ((x.wrapping_mul(17) ^ y.wrapping_mul(31)) & 3) - 1
            };
            let gradient = (5.0 * (1.0 - y as f64 / bounds.height as f64)).round() as i32 + grain;
            let r = (settings.background_color.r + gradient).clamp(0, 255) as u32;
            let g = (settings.background_color.g + gradient).clamp(0, 255) as u32;
            let b = (settings.background_color.b + gradient).clamp(0, 255) as u32;
            let tint = r << 16 | g << 8 | b;
            *pixel = if window.material() == SurfaceStyle::Frosted {
                compose_pixel(
                    *pixel,
                    crate::blur::tint(
                        renderer.pixel(bounds.x + x, bounds.y + y).unwrap_or(tint),
                        tint,
                        opacity,
                    ),
                    1.0,
                    a,
                )
            } else {
                compose_pixel(*pixel, tint, opacity, a)
            };
        }
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
            assert!(draw(&mut renderer, &window, bounds, 1.0, &model, &settings).unwrap());
            window.set_visible(true);
            std::thread::sleep(Duration::from_millis(80));
            renderer.backdrop.as_mut().unwrap().sampled = Instant::now() - BACKGROUND_INTERVAL;
            // A second capture with our card now visible must still see only
            // the stripes; otherwise it recursively blurs its own text.
            assert!(
                !draw(&mut renderer, &window, bounds, 1.0, &model, &settings).unwrap(),
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
                            &settings
                        )
                        .unwrap());
                    }
                }
                renderer.clear();
            }
            eprintln!("Clock-only expanded native drawing verified: 3 materials, 3 scales, 3 resize heights.");
        }
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
