fn main() {
    // A generated native icon, not a source-controlled binary.
    let mut pixels = vec![0u8; 32 * 32 * 4];
    for y in 0..32usize {
        for x in 0..32usize {
            let i = ((31 - y) * 32 + x) * 4;
            let white = (x as i32 + y as i32 - 31).abs() < 3 && (7..25).contains(&x);
            pixels[i..i + 4].copy_from_slice(if white {
                &[255, 255, 255, 255]
            } else {
                &[237, 106, 61, 255]
            });
        }
    }
    let size = 40 + pixels.len() + 128;
    let mut ico = vec![0, 0, 1, 0, 1, 0, 32, 32, 0, 0, 1, 0, 32, 0];
    ico.extend_from_slice(&(size as u32).to_le_bytes());
    ico.extend_from_slice(&22u32.to_le_bytes());
    let mut dib = vec![0u8; 40];
    dib[0..4].copy_from_slice(&40u32.to_le_bytes());
    dib[4..8].copy_from_slice(&32i32.to_le_bytes());
    dib[8..12].copy_from_slice(&64i32.to_le_bytes());
    dib[12..14].copy_from_slice(&1u16.to_le_bytes());
    dib[14..16].copy_from_slice(&32u16.to_le_bytes());
    ico.extend(dib);
    ico.extend(pixels);
    ico.extend([0u8; 128]);
    std::fs::create_dir_all("target").unwrap();
    std::fs::write("target/board.ico", ico).unwrap();
    tauri_build::build()
}
