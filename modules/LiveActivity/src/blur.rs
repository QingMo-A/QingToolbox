//! Bounded, separable three-box approximation of a Gaussian. RGB only:
//! foreground glyphs are composited afterwards and are never blurred.
pub const MAX_PIXELS: usize = 1_048_576;

pub fn gaussian(
    pixels: &[u32],
    width: usize,
    height: usize,
    radius: usize,
) -> Result<Vec<u32>, &'static str> {
    let length = width
        .checked_mul(height)
        .ok_or("blur dimensions overflow")?;
    if width == 0 || height == 0 || length > MAX_PIXELS || pixels.len() != length {
        return Err("invalid blur dimensions");
    }
    let radius = radius.min(32);
    let mut current = pixels.to_vec();
    let mut scratch = vec![0; length];
    for _ in 0..3 {
        pass(&current, &mut scratch, width, height, radius, true);
        std::mem::swap(&mut current, &mut scratch);
        pass(&current, &mut scratch, width, height, radius, false);
        std::mem::swap(&mut current, &mut scratch);
    }
    scratch.fill(0);
    Ok(current)
}

fn pass(
    input: &[u32],
    output: &mut [u32],
    width: usize,
    height: usize,
    radius: usize,
    horizontal: bool,
) {
    let (lines, span) = if horizontal {
        (height, width)
    } else {
        (width, height)
    };
    let kernel = (radius * 2 + 1) as u32;
    for line in 0..lines {
        let at = |offset: usize| {
            if horizontal {
                line * width + offset
            } else {
                offset * width + line
            }
        };
        let mut sum = [0u32; 3];
        for offset in -(radius as isize)..=radius as isize {
            let pixel = input[at(offset.clamp(0, span as isize - 1) as usize)];
            for (channel, value) in sum.iter_mut().enumerate() {
                *value += (pixel >> (channel * 8)) & 255;
            }
        }
        for offset in 0..span {
            let mut pixel = 0xFF000000;
            for (channel, value) in sum.iter().enumerate() {
                pixel |= (value / kernel) << (channel * 8);
            }
            output[at(offset)] = pixel;
            let remove = input[at(offset.saturating_sub(radius))];
            let add = input[at(offset.saturating_add(radius + 1).min(span - 1))];
            for (channel, value) in sum.iter_mut().enumerate() {
                *value =
                    *value - ((remove >> (channel * 8)) & 255) + ((add >> (channel * 8)) & 255);
            }
        }
    }
}

pub fn tint(background: u32, color: u32, opacity: f64) -> u32 {
    let opacity = opacity.clamp(0.0, 1.0);
    let mut result = 0;
    for shift in [0, 8, 16] {
        let a = ((background >> shift) & 255) as f64;
        let b = ((color >> shift) & 255) as f64;
        result |= ((a * (1.0 - opacity) + b * opacity).round() as u32) << shift;
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn constants_and_single_pixel_are_preserved() {
        for (w, h) in [(1, 1), (1, 15), (17, 1), (17, 15)] {
            assert!(gaussian(&vec![0x123456; w * h], w, h, 6)
                .unwrap()
                .iter()
                .all(|&p| p & 0xFFFFFF == 0x123456));
        }
    }
    #[test]
    fn stripes_are_blurred_without_mixing_rgb_channels() {
        let pixels: Vec<_> = (0..40)
            .map(|x| if x % 2 == 0 { 0xFF0000 } else { 0 })
            .collect();
        let output = gaussian(&pixels, 40, 1, 5).unwrap();
        assert!(output[10..30]
            .iter()
            .all(|&p| (p >> 16) & 255 > 80 && (p >> 16) & 255 < 170 && p & 0xFFFF == 0));
    }
    #[test]
    fn step_edge_remains_a_step_not_a_flat_average() {
        let pixels: Vec<_> = (0..100)
            .map(|x| if x < 50 { 0xFF0000 } else { 0x0000FF })
            .collect();
        let output = gaussian(&pixels, 100, 1, 5).unwrap();
        assert_eq!(output[0] & 0xFFFFFF, 0xFF0000);
        assert_eq!(output[99] & 0xFFFFFF, 0x0000FF);
        assert!(output[49] & 255 > 0 && (output[49] >> 16) & 255 > 0);
    }
    #[test]
    fn malformed_images_are_bounded_and_tint_is_not_alpha() {
        assert!(gaussian(&[], 0, 2, 5).is_err());
        assert!(gaussian(&[], usize::MAX, 2, 5).is_err());
        assert!(gaussian(&[1], 2, 2, 5).is_err());
        assert_eq!(tint(0xFFFFFF, 0, 0.5), 0x808080);
    }
}
