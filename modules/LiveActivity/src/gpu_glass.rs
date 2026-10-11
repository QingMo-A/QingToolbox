//! Native desktop glass: DXGI -> D3D11 shaders -> DirectComposition.
//! No desktop pixels are read back to the CPU. The only uploaded image is our
//! own premultiplied glyph layer. See third-party/GLASS-SOURCES.md for design
//! references; this Rust integration and its shader are independently written.
#![cfg(windows)]

use crate::{
    overlay::Bounds,
    settings::{Settings, SurfaceStyle},
};
use std::time::{Duration, Instant};
use windows::{
    core::{Interface, PCSTR},
    Win32::{
        Foundation::{HMODULE, HWND, RECT},
        Graphics::{
            Direct3D::Fxc::{D3DCompile, D3DCOMPILE_OPTIMIZATION_LEVEL3},
            Direct3D::{
                ID3DBlob, D3D_DRIVER_TYPE_UNKNOWN, D3D_FEATURE_LEVEL_11_0,
                D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST,
            },
            Direct3D11::*,
            DirectComposition::*,
            Dxgi::Common::*,
            Dxgi::*,
        },
    },
};

const SHADER: &str = include_str!("glass.hlsl");
pub const CADENCE: Duration = Duration::from_millis(33);

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct Params {
    region: [f32; 4],
    output: [f32; 4],
    panel: [f32; 4],
    config: [f32; 4],
    tint: [f32; 4],
    mouse: [f32; 4],
    viewport: [f32; 4],
}

struct Texture {
    texture: ID3D11Texture2D,
    srv: ID3D11ShaderResourceView,
    rtv: Option<ID3D11RenderTargetView>,
    width: u32,
    height: u32,
}
impl Texture {
    unsafe fn new(
        device: &ID3D11Device,
        width: u32,
        height: u32,
        target: bool,
    ) -> windows::core::Result<Self> {
        let desc = D3D11_TEXTURE2D_DESC {
            Width: width,
            Height: height,
            MipLevels: 1,
            ArraySize: 1,
            Format: DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: (D3D11_BIND_SHADER_RESOURCE.0
                | if target {
                    D3D11_BIND_RENDER_TARGET.0
                } else {
                    0
                }) as u32,
            ..Default::default()
        };
        let mut texture = None;
        device.CreateTexture2D(&desc, None, Some(&mut texture))?;
        let texture = texture.unwrap();
        let mut srv = None;
        device.CreateShaderResourceView(&texture, None, Some(&mut srv))?;
        let mut rtv = None;
        if target {
            device.CreateRenderTargetView(&texture, None, Some(&mut rtv))?;
        }
        Ok(Self {
            texture,
            srv: srv.unwrap(),
            rtv,
            width,
            height,
        })
    }
}

struct Capture {
    output: IDXGIOutput1,
    desc: DXGI_OUTPUT_DESC,
    duplication: Option<IDXGIOutputDuplication>,
    snapshot: Option<Texture>,
    previous: Option<Texture>,
    retry: Instant,
    unavailable_since: Option<Instant>,
    generation: u64,
}
impl Capture {
    unsafe fn next(
        &mut self,
        device: &ID3D11Device,
        context: &ID3D11DeviceContext,
    ) -> windows::core::Result<bool> {
        if self.duplication.is_none() {
            if Instant::now() < self.retry {
                return Ok(false);
            }
            match self.output.DuplicateOutput(device) {
                Ok(duplication) => {
                    self.duplication = Some(duplication);
                    self.snapshot = None;
                    self.unavailable_since = None;
                    crate::diagnostics::information("overlay/dxgi", "desktop duplication acquired");
                }
                Err(error) => {
                    self.retry = Instant::now() + Duration::from_millis(500);
                    let since = self.unavailable_since.get_or_insert_with(Instant::now);
                    if since.elapsed() > Duration::from_secs(8) {
                        return Err(error);
                    }
                    return Ok(false);
                }
            }
        }
        let duplication = self.duplication.as_ref().unwrap();
        let mut info = DXGI_OUTDUPL_FRAME_INFO::default();
        let mut resource = None;
        match duplication.AcquireNextFrame(0, &mut info, &mut resource) {
            Err(error) if error.code() == DXGI_ERROR_WAIT_TIMEOUT => return Ok(false),
            Err(error) if error.code() == DXGI_ERROR_ACCESS_LOST => {
                self.duplication = None;
                self.retry = Instant::now() + Duration::from_millis(100);
                crate::diagnostics::information(
                    "overlay/dxgi",
                    "display/session changed; rebuilding duplication",
                );
                return Ok(false);
            }
            Err(error) => return Err(error),
            Ok(()) => (),
        }
        // Release every acquired frame, including error and cursor-only paths.
        // Hardware cursor data is intentionally never painted into the glass.
        let result = (|| {
            let source: ID3D11Texture2D = resource.unwrap().cast()?;
            if info.LastPresentTime == 0 && self.snapshot.is_some() {
                return Ok(false);
            }
            let mut desc = D3D11_TEXTURE2D_DESC::default();
            source.GetDesc(&mut desc);
            std::mem::swap(&mut self.snapshot, &mut self.previous);
            if self
                .snapshot
                .as_ref()
                .map_or(true, |s| s.width != desc.Width || s.height != desc.Height)
            {
                self.snapshot = Some(Texture::new(device, desc.Width, desc.Height, false)?);
            }
            if self
                .previous
                .as_ref()
                .is_some_and(|s| s.width != desc.Width || s.height != desc.Height)
            {
                self.previous = None;
            }
            context.CopyResource(&self.snapshot.as_ref().unwrap().texture, &source);
            Ok(true)
        })();
        let released = duplication.ReleaseFrame();
        result.and_then(|changed| released.map(|_| changed))
    }
}

/// Owned exclusively by the existing overlay thread. COM interfaces and the
/// immediate context never migrate between threads or outlive their HWND.
pub struct Glass {
    device: ID3D11Device,
    context: ID3D11DeviceContext,
    capture: Capture,
    composition: IDCompositionDevice,
    target: IDCompositionTarget,
    visual: IDCompositionVisual,
    swap: IDXGISwapChain1,
    vertex: ID3D11VertexShader,
    horizontal: ID3D11PixelShader,
    vertical: ID3D11PixelShader,
    glass: ID3D11PixelShader,
    compare: ID3D11ComputeShader,
    change: ID3D11Buffer,
    change_view: ID3D11UnorderedAccessView,
    change_readback: ID3D11Buffer,
    sampler: ID3D11SamplerState,
    constants: ID3D11Buffer,
    blur_a: Option<Texture>,
    blur_b: Option<Texture>,
    ink: Option<Texture>,
    swap_size: (u32, u32),
    last_ink: Vec<u32>,
    last_capture: Instant,
    last_key: Option<String>,
    pub samples: u64,
    pub frames: u64,
    #[cfg(test)]
    pub probe: bool,
    #[cfg(test)]
    pub test_pixels: Vec<u32>,
}

/// Select the adapter actually driving this monitor, not the first GPU.
unsafe fn select_output(
    bounds: Bounds,
) -> windows::core::Result<(IDXGIAdapter1, IDXGIOutput1, DXGI_OUTPUT_DESC)> {
    let factory: IDXGIFactory1 = CreateDXGIFactory1()?;
    let (x, y) = (bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
    for ai in 0..32 {
        let adapter = match factory.EnumAdapters1(ai) {
            Ok(a) => a,
            Err(_) => break,
        };
        for oi in 0..32 {
            let output = match adapter.EnumOutputs(oi) {
                Ok(o) => o,
                Err(_) => break,
            };
            let desc = output.GetDesc()?;
            if desc.AttachedToDesktop.as_bool() && contains(desc.DesktopCoordinates, x, y) {
                return Ok((adapter, output.cast()?, desc));
            }
        }
    }
    Err(windows::core::Error::from_hresult(DXGI_ERROR_NOT_FOUND))
}
fn contains(r: RECT, x: i32, y: i32) -> bool {
    x >= r.left && x < r.right && y >= r.top && y < r.bottom
}

unsafe fn compile(entry: &'static [u8], target: &'static [u8]) -> windows::core::Result<ID3DBlob> {
    let (mut code, mut errors) = (None, None);
    let result = D3DCompile(
        SHADER.as_ptr().cast(),
        SHADER.len(),
        PCSTR::null(),
        None,
        None,
        PCSTR(entry.as_ptr()),
        PCSTR(target.as_ptr()),
        D3DCOMPILE_OPTIMIZATION_LEVEL3,
        0,
        &mut code,
        Some(&mut errors),
    );
    if let Err(error) = result {
        if let Some(errors) = errors {
            let message = String::from_utf8_lossy(std::slice::from_raw_parts(
                errors.GetBufferPointer().cast(),
                errors.GetBufferSize(),
            ));
            crate::diagnostics::error("overlay/shader", &message);
        }
        return Err(error);
    }
    Ok(code.unwrap())
}
unsafe fn bytecode(blob: &ID3DBlob) -> &[u8] {
    std::slice::from_raw_parts(blob.GetBufferPointer().cast(), blob.GetBufferSize())
}

impl Glass {
    pub unsafe fn new(
        handle: windows_sys::Win32::Foundation::HWND,
        bounds: Bounds,
    ) -> Result<Self, String> {
        Self::create(HWND(handle), bounds).map_err(|e| format!("GPU 玻璃初始化失败：{e}"))
    }
    unsafe fn create(handle: HWND, bounds: Bounds) -> windows::core::Result<Self> {
        let (adapter, output, desc) = select_output(bounds)?;
        let (mut device, mut context) = (None, None);
        D3D11CreateDevice(
            &adapter,
            D3D_DRIVER_TYPE_UNKNOWN,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            Some(&[D3D_FEATURE_LEVEL_11_0]),
            D3D11_SDK_VERSION,
            Some(&mut device),
            None,
            Some(&mut context),
        )?;
        let device = device.unwrap();
        let context = context.unwrap();
        let dxgi: IDXGIDevice = device.cast()?;
        let factory: IDXGIFactory2 = adapter.GetParent()?;
        let swap = factory.CreateSwapChainForComposition(
            &device,
            &DXGI_SWAP_CHAIN_DESC1 {
                Width: 1,
                Height: 1,
                Format: DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc: DXGI_SAMPLE_DESC {
                    Count: 1,
                    Quality: 0,
                },
                BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount: 2,
                Scaling: DXGI_SCALING_STRETCH,
                SwapEffect: DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                AlphaMode: DXGI_ALPHA_MODE_PREMULTIPLIED,
                ..Default::default()
            },
            None,
        )?;
        // Flip-model buffers have undefined initial content. Clear both before
        // attaching/showing the visual, including the first-frame timeout case.
        for _ in 0..2 {
            let buffer: ID3D11Texture2D = swap.GetBuffer(0)?;
            let mut target = None;
            device.CreateRenderTargetView(&buffer, None, Some(&mut target))?;
            context.ClearRenderTargetView(target.as_ref().unwrap(), &[0.0; 4]);
            swap.Present(0, DXGI_PRESENT(0)).ok()?;
        }
        let composition: IDCompositionDevice = DCompositionCreateDevice(&dxgi)?;
        let target = composition.CreateTargetForHwnd(handle, true)?;
        let visual = composition.CreateVisual()?;
        visual.SetContent(&swap)?;
        target.SetRoot(&visual)?;
        composition.Commit()?;
        let vs = compile(b"Vertex\0", b"vs_5_0\0")?;
        let mut vertex = None;
        device.CreateVertexShader(bytecode(&vs), None, Some(&mut vertex))?;
        let make_ps = |entry| -> windows::core::Result<ID3D11PixelShader> {
            let blob = compile(entry, b"ps_5_0\0")?;
            let mut shader = None;
            device.CreatePixelShader(bytecode(&blob), None, Some(&mut shader))?;
            Ok(shader.unwrap())
        };
        let horizontal = make_ps(b"Horizontal\0")?;
        let vertical = make_ps(b"Vertical\0")?;
        let glass = make_ps(b"Glass\0")?;
        let blob = compile(b"Compare\0", b"cs_5_0\0")?;
        let mut compare = None;
        device.CreateComputeShader(bytecode(&blob), None, Some(&mut compare))?;
        let mut change = None;
        device.CreateBuffer(
            &D3D11_BUFFER_DESC {
                ByteWidth: 4,
                Usage: D3D11_USAGE_DEFAULT,
                BindFlags: D3D11_BIND_UNORDERED_ACCESS.0 as u32,
                ..Default::default()
            },
            None,
            Some(&mut change),
        )?;
        let change = change.unwrap();
        let mut change_view = None;
        device.CreateUnorderedAccessView(
            &change,
            Some(&D3D11_UNORDERED_ACCESS_VIEW_DESC {
                Format: DXGI_FORMAT_R32_UINT,
                ViewDimension: D3D11_UAV_DIMENSION_BUFFER,
                Anonymous: D3D11_UNORDERED_ACCESS_VIEW_DESC_0 {
                    Buffer: D3D11_BUFFER_UAV {
                        FirstElement: 0,
                        NumElements: 1,
                        Flags: 0,
                    },
                },
            }),
            Some(&mut change_view),
        )?;
        let mut change_readback = None;
        device.CreateBuffer(
            &D3D11_BUFFER_DESC {
                ByteWidth: 4,
                Usage: D3D11_USAGE_STAGING,
                CPUAccessFlags: D3D11_CPU_ACCESS_READ.0 as u32,
                ..Default::default()
            },
            None,
            Some(&mut change_readback),
        )?;
        let mut sampler = None;
        device.CreateSamplerState(
            &D3D11_SAMPLER_DESC {
                Filter: D3D11_FILTER_MIN_MAG_MIP_LINEAR,
                AddressU: D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressV: D3D11_TEXTURE_ADDRESS_CLAMP,
                AddressW: D3D11_TEXTURE_ADDRESS_CLAMP,
                MaxLOD: f32::MAX,
                ..Default::default()
            },
            Some(&mut sampler),
        )?;
        let mut constants = None;
        device.CreateBuffer(
            &D3D11_BUFFER_DESC {
                ByteWidth: std::mem::size_of::<Params>() as u32,
                Usage: D3D11_USAGE_DEFAULT,
                BindFlags: D3D11_BIND_CONSTANT_BUFFER.0 as u32,
                ..Default::default()
            },
            None,
            Some(&mut constants),
        )?;
        crate::diagnostics::information(
            "overlay/gpu",
            "DXGI / D3D11 / DirectComposition ready; backdrop stays on GPU",
        );
        Ok(Self {
            device,
            context,
            capture: Capture {
                output,
                desc,
                duplication: None,
                snapshot: None,
                previous: None,
                retry: Instant::now(),
                unavailable_since: None,
                generation: 0,
            },
            composition,
            target,
            visual,
            swap,
            vertex: vertex.unwrap(),
            horizontal,
            vertical,
            glass,
            compare: compare.unwrap(),
            change,
            change_view: change_view.unwrap(),
            change_readback: change_readback.unwrap(),
            sampler: sampler.unwrap(),
            constants: constants.unwrap(),
            blur_a: None,
            blur_b: None,
            ink: None,
            swap_size: (1, 1),
            last_ink: Vec::new(),
            last_capture: Instant::now() - CADENCE,
            last_key: None,
            samples: 0,
            frames: 0,
            #[cfg(test)]
            probe: false,
            #[cfg(test)]
            test_pixels: Vec::new(),
        })
    }
    pub fn until_refresh(&self) -> Duration {
        CADENCE.saturating_sub(self.last_capture.elapsed())
    }
    pub fn matches_output(&self, bounds: Bounds) -> bool {
        contains(
            self.capture.desc.DesktopCoordinates,
            bounds.x + bounds.width / 2,
            bounds.y + bounds.height / 2,
        )
    }
    pub fn invalidate_capture(&mut self) {
        if let Ok(desc) = unsafe { self.capture.output.GetDesc() } {
            self.capture.desc = desc;
        }
        self.capture.duplication = None;
        self.capture.snapshot = None;
        self.capture.previous = None;
        self.capture.retry = Instant::now();
        self.last_key = None;
    }
    pub unsafe fn draw(
        &mut self,
        bounds: Bounds,
        scale: f64,
        settings: &Settings,
        pointer: Option<(f64, f64)>,
        foreground: &[u32],
    ) -> Result<bool, String> {
        self.present(bounds, scale, settings, pointer, foreground)
            .map_err(|e| format!("GPU 玻璃采集或合成失败：{e}"))
    }
    unsafe fn pass(
        &self,
        shader: &ID3D11PixelShader,
        target: &ID3D11RenderTargetView,
        resources: &[Option<ID3D11ShaderResourceView>],
        mut params: Params,
        width: u32,
        height: u32,
    ) {
        self.context
            .PSSetShaderResources(0, Some(&[None, None, None]));
        self.context
            .OMSetRenderTargets(Some(&[Some(target.clone())]), None);
        params.viewport = [width as f32, height as f32, 0.0, 0.0];
        self.context.UpdateSubresource(
            &self.constants,
            0,
            None,
            (&params as *const Params).cast(),
            0,
            0,
        );
        self.context.RSSetViewports(Some(&[D3D11_VIEWPORT {
            Width: width as f32,
            Height: height as f32,
            MaxDepth: 1.0,
            ..Default::default()
        }]));
        self.context.PSSetShader(Some(shader), None);
        self.context.PSSetShaderResources(0, Some(resources));
        self.context.Draw(3, 0);
        self.context
            .PSSetShaderResources(0, Some(&[None, None, None]));
        self.context.OMSetRenderTargets(None, None);
    }
    unsafe fn present(
        &mut self,
        bounds: Bounds,
        scale: f64,
        settings: &Settings,
        pointer: Option<(f64, f64)>,
        foreground: &[u32],
    ) -> windows::core::Result<bool> {
        let mut acquired = false;
        if self.until_refresh().is_zero() || self.capture.snapshot.is_none() {
            self.last_capture = Instant::now();
            acquired = self.capture.next(&self.device, &self.context)?;
            if acquired {
                self.samples += 1;
            }
        }
        if acquired && self.backdrop_changed(bounds, scale)? {
            self.capture.generation = self.capture.generation.wrapping_add(1);
        }
        let Some(snapshot) = self.capture.snapshot.as_ref() else {
            return Ok(false);
        };
        let ink_changed = self.last_ink != foreground;
        let key = format!(
            "{bounds:?}|{scale}|{:?}|{}|{}|{:?}|{}|{:?}",
            settings.surface_style,
            settings.background_opacity,
            settings.frost_blur,
            pointer,
            self.capture.generation,
            settings.background_color
        );
        if !ink_changed && self.last_key.as_deref() == Some(&key) {
            return Ok(false);
        }
        let (width, height) = (bounds.width as u32, bounds.height as u32);
        if self.swap_size != (width, height) {
            self.context.ClearState();
            self.swap.ResizeBuffers(
                0,
                width,
                height,
                DXGI_FORMAT_UNKNOWN,
                DXGI_SWAP_CHAIN_FLAG(0),
            )?;
            self.swap_size = (width, height);
            self.ink = None;
        }
        let new_ink = self.ink.is_none();
        if new_ink {
            self.ink = Some(Texture::new(&self.device, width, height, false)?);
        }
        let ink = self.ink.as_ref().unwrap();
        if ink_changed || new_ink || self.last_key.is_none() {
            self.context.UpdateSubresource(
                &ink.texture,
                0,
                None,
                foreground.as_ptr().cast(),
                width * 4,
                0,
            );
            self.last_ink.clear();
            self.last_ink.extend_from_slice(foreground);
        }
        let region = padded_region(bounds, scale);
        let (bw, bh) = (
            ((region.width + 1) / 2) as u32,
            ((region.height + 1) / 2) as u32,
        );
        if self
            .blur_a
            .as_ref()
            .map_or(true, |t| (t.width, t.height) != (bw, bh))
        {
            self.blur_a = Some(Texture::new(&self.device, bw, bh, true)?);
            self.blur_b = Some(Texture::new(&self.device, bw, bh, true)?);
        }
        let a = self.blur_a.as_ref().unwrap();
        let b = self.blur_b.as_ref().unwrap();
        let r = self.capture.desc.DesktopCoordinates;
        let liquid = settings.surface_style == SurfaceStyle::Liquid;
        let color = settings.background_color;
        let mut params = Params {
            region: [
                region.x as f32,
                region.y as f32,
                region.width as f32,
                region.height as f32,
            ],
            output: [
                r.left as f32,
                r.top as f32,
                (r.right - r.left) as f32,
                (r.bottom - r.top) as f32,
            ],
            panel: [
                bounds.x as f32,
                bounds.y as f32,
                width as f32,
                height as f32,
            ],
            config: [
                crate::renderer::island_radius(bounds.height, scale) as f32,
                scale as f32,
                if liquid {
                    0.75 * scale as f32
                } else {
                    settings.frost_blur as f32 * scale as f32
                },
                f32::from(liquid),
            ],
            tint: [
                color.r as f32 / 255.0,
                color.g as f32 / 255.0,
                color.b as f32 / 255.0,
                ((settings.background_opacity - 0.35) / 0.65).clamp(0.0, 1.0) as f32
                    * if liquid { 0.4 } else { 0.72 },
            ],
            mouse: [
                pointer.map_or(0.0, |p| p.0) as f32,
                pointer.map_or(0.0, |p| p.1) as f32,
                f32::from(pointer.is_some()),
                self.capture.desc.Rotation.0 as f32,
            ],
            ..Default::default()
        };
        self.context.IASetInputLayout(None);
        self.context
            .IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        self.context.VSSetShader(Some(&self.vertex), None);
        self.context
            .PSSetSamplers(0, Some(&[Some(self.sampler.clone())]));
        self.context
            .PSSetConstantBuffers(0, Some(&[Some(self.constants.clone())]));
        self.pass(
            &self.horizontal,
            a.rtv.as_ref().unwrap(),
            &[Some(snapshot.srv.clone())],
            params,
            bw,
            bh,
        );
        self.pass(
            &self.vertical,
            b.rtv.as_ref().unwrap(),
            &[Some(a.srv.clone())],
            params,
            bw,
            bh,
        );
        let buffer: ID3D11Texture2D = self.swap.GetBuffer(0)?;
        let mut target = None;
        self.device
            .CreateRenderTargetView(&buffer, None, Some(&mut target))?;
        // All constants, including color, participate in frame invalidation.
        params.viewport = [width as f32, height as f32, 0.0, 0.0];
        self.pass(
            &self.glass,
            target.as_ref().unwrap(),
            &[
                Some(snapshot.srv.clone()),
                Some(b.srv.clone()),
                Some(ink.srv.clone()),
            ],
            params,
            width,
            height,
        );
        #[cfg(test)]
        if self.probe {
            self.test_pixels = self.read_pixels(&buffer)?;
        }
        self.swap.Present(0, DXGI_PRESENT(0)).ok()?;
        self.composition.Commit()?;
        self.last_key = Some(key);
        self.frames += 1;
        Ok(true)
    }
    /// Compare only the padded capsule area on the GPU. Read back a four-byte
    /// changed/not-changed flag, never desktop pixels. Unlike dropping dirty
    /// rectangles matching our HWND, this cannot discard real updates behind
    /// the capsule. It also breaks DWM's self-Present notification loop.
    unsafe fn backdrop_changed(&self, bounds: Bounds, scale: f64) -> windows::core::Result<bool> {
        let Some(previous) = self.capture.previous.as_ref() else {
            return Ok(true);
        };
        let snapshot = self.capture.snapshot.as_ref().unwrap();
        let region = padded_region(bounds, scale);
        let r = self.capture.desc.DesktopCoordinates;
        let params = Params {
            region: [
                region.x as f32,
                region.y as f32,
                region.width as f32,
                region.height as f32,
            ],
            output: [
                r.left as f32,
                r.top as f32,
                (r.right - r.left) as f32,
                (r.bottom - r.top) as f32,
            ],
            mouse: [0.0, 0.0, 0.0, self.capture.desc.Rotation.0 as f32],
            ..Default::default()
        };
        self.context.UpdateSubresource(
            &self.constants,
            0,
            None,
            (&params as *const Params).cast(),
            0,
            0,
        );
        self.context
            .ClearUnorderedAccessViewUint(&self.change_view, &[0; 4]);
        self.context.CSSetShader(Some(&self.compare), None);
        self.context
            .CSSetConstantBuffers(0, Some(&[Some(self.constants.clone())]));
        self.context
            .CSSetSamplers(0, Some(&[Some(self.sampler.clone())]));
        self.context.CSSetShaderResources(
            0,
            Some(&[Some(previous.srv.clone()), Some(snapshot.srv.clone())]),
        );
        self.context.CSSetUnorderedAccessViews(
            0,
            1,
            Some([Some(self.change_view.clone())].as_ptr()),
            None,
        );
        self.context.Dispatch(
            (region.width as u32 + 15) / 16,
            (region.height as u32 + 15) / 16,
            1,
        );
        self.context
            .CSSetUnorderedAccessViews(0, 1, Some([None].as_ptr()), None);
        self.context.CSSetShaderResources(0, Some(&[None, None]));
        self.context.CSSetShader(None, None);
        self.context
            .CopyResource(&self.change_readback, &self.change);
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        self.context.Map(
            &self.change_readback,
            0,
            D3D11_MAP_READ,
            0,
            Some(&mut mapped),
        )?;
        let changed = *(mapped.pData as *const u32) != 0;
        self.context.Unmap(&self.change_readback, 0);
        Ok(changed)
    }
    #[cfg(test)]
    unsafe fn read_pixels(&self, texture: &ID3D11Texture2D) -> windows::core::Result<Vec<u32>> {
        let mut desc = D3D11_TEXTURE2D_DESC::default();
        texture.GetDesc(&mut desc);
        desc.Usage = D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.MiscFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ.0 as u32;
        let mut staging = None;
        self.device
            .CreateTexture2D(&desc, None, Some(&mut staging))?;
        let staging = staging.unwrap();
        self.context.CopyResource(&staging, texture);
        let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
        self.context
            .Map(&staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped))?;
        let mut pixels = Vec::with_capacity((desc.Width * desc.Height) as usize);
        for y in 0..desc.Height {
            let row = (mapped.pData as *const u8)
                .add((y * mapped.RowPitch) as usize)
                .cast::<u32>();
            pixels.extend_from_slice(std::slice::from_raw_parts(row, desc.Width as usize));
        }
        self.context.Unmap(&staging, 0);
        Ok(pixels)
    }
}
impl Drop for Glass {
    fn drop(&mut self) {
        unsafe {
            self.context.ClearState();
            let _ = self.target.SetRoot(None);
            let _ = self.visual.SetContent(None);
            let _ = self.composition.Commit();
        }
        self.last_ink.fill(0);
    }
}
fn padded_region(bounds: Bounds, scale: f64) -> Bounds {
    // 40 logical px sigma reaches 80 px at the last kernel tap. Keep enough
    // real surrounding pixels even at the maximum blur setting and high DPI.
    let pad = (96.0 * scale).ceil().clamp(16.0, 768.0) as i32;
    Bounds {
        x: bounds.x - pad,
        y: bounds.y - pad,
        width: bounds.width + pad * 2,
        height: bounds.height + pad * 2,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn padding_and_negative_monitor_coordinates_are_physical() {
        let b = Bounds {
            x: -1800,
            y: -240,
            width: 600,
            height: 48,
        };
        assert_eq!(
            padded_region(b, 1.5),
            Bounds {
                x: -1944,
                y: -384,
                width: 888,
                height: 336
            }
        );
        assert!(contains(
            RECT {
                left: -1920,
                top: -1080,
                right: 0,
                bottom: 0
            },
            -1,
            -1
        ));
        assert!(!contains(
            RECT {
                left: -1920,
                top: -1080,
                right: 0,
                bottom: 0
            },
            0,
            0
        ));
        assert_eq!(std::mem::size_of::<Params>() % 16, 0);
    }
    #[test]
    fn shader_compiles_for_both_materials() {
        unsafe {
            compile(b"Vertex\0", b"vs_5_0\0").unwrap();
            compile(b"Compare\0", b"cs_5_0\0").unwrap();
            for entry in [b"Horizontal\0".as_slice(), b"Vertical\0", b"Glass\0"] {
                compile(entry, b"ps_5_0\0").unwrap();
            }
        }
    }
}

/// Integration test over a window owned by this test, not a screenshot of the
/// user's apps. Inspect the GPU's final render target before Present.
#[cfg(test)]
pub fn test_live_capture() {
    use windows_sys::Win32::{
        Foundation::HWND as RawHwnd, Graphics::Gdi::*, UI::WindowsAndMessaging::*,
    };
    struct Background(RawHwnd);
    impl Drop for Background {
        fn drop(&mut self) {
            unsafe {
                DestroyWindow(self.0);
            }
        }
    }
    unsafe extern "system" fn paint_background(
        hwnd: RawHwnd,
        message: u32,
        w: usize,
        l: isize,
    ) -> isize {
        if message == WM_PAINT {
            let mut paint = PAINTSTRUCT::default();
            let dc = BeginPaint(hwnd, &mut paint);
            let brush = CreateSolidBrush(GetWindowLongPtrW(hwnd, GWLP_USERDATA) as u32);
            FillRect(dc, &paint.rcPaint, brush);
            DeleteObject(brush);
            EndPaint(hwnd, &paint);
            return 0;
        }
        DefWindowProcW(hwnd, message, w, l)
    }
    crate::win32::enable_dpi_awareness();
    let monitor = crate::win32::primary_monitor().unwrap();
    let bounds = Bounds {
        x: monitor.x + 140,
        y: monitor.y + 220,
        width: 300,
        height: 120,
    };
    unsafe {
        let class: Vec<u16> = "QingIslandGpuTestBackdrop\0".encode_utf16().collect();
        RegisterClassW(&WNDCLASSW {
            lpfnWndProc: Some(paint_background),
            lpszClassName: class.as_ptr(),
            ..Default::default()
        });
        let background = Background(CreateWindowExW(
            WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED,
            class.as_ptr(),
            std::ptr::null(),
            WS_POPUP,
            bounds.x - 100,
            bounds.y - 100,
            bounds.width + 200,
            bounds.height + 200,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            std::ptr::null(),
        ));
        assert!(!background.0.is_null());
        SetLayeredWindowAttributes(background.0, 0, 255, LWA_ALPHA);
        ShowWindow(background.0, SW_SHOWNOACTIVATE);
        let paint = |color| {
            SetWindowLongPtrW(background.0, GWLP_USERDATA, color as isize);
            let dc = GetDC(background.0);
            let brush = CreateSolidBrush(color);
            FillRect(
                dc,
                &windows_sys::Win32::Foundation::RECT {
                    left: 0,
                    top: 0,
                    right: 500,
                    bottom: 320,
                },
                brush,
            );
            DeleteObject(brush);
            ReleaseDC(background.0, dc);
            GdiFlush();
            windows_sys::Win32::Graphics::Dwm::DwmFlush();
        };
        for material in [SurfaceStyle::Frosted, SurfaceStyle::Liquid] {
            paint(0x3030D0);
            let window = crate::win32::IslandWindow::create(material).unwrap();
            window.apply(bounds, true, 1.0).unwrap();
            window.set_controls(vec![Bounds {
                x: 0,
                y: 0,
                width: bounds.width,
                height: bounds.height,
            }]);
            window.set_click_through(true);
            let settings = Settings {
                surface_style: material,
                background_opacity: 0.35,
                ..Default::default()
            };
            let mut gpu = Glass::new(window.handle(), bounds).unwrap();
            gpu.probe = true;
            let mut ink = vec![0u32; (bounds.width * bounds.height) as usize];
            for y in 55..65 {
                for x in 70..80 {
                    ink[(y * bounds.width + x) as usize] = 0xFFFFFFFF;
                }
            }
            let mut tick = || {
                for _ in 0..6 {
                    crate::win32::pump_messages();
                    gpu.draw(bounds, 1.0, &settings, None, &ink).unwrap();
                    window.set_visible(true);
                    std::thread::sleep(Duration::from_millis(65));
                }
            };
            tick();
            let first = gpu.test_pixels.clone();
            let before = gpu.frames;
            let at = |pixels: &[u32], x: i32, y: i32| pixels[(y * bounds.width + x) as usize];
            let red = at(&first, 200, 60);
            eprintln!("{material:?} initial GPU center: {red:08x}");
            assert!(
                ((red >> 16) & 255) > (red & 255) + 70,
                "DXGI did not capture the red window: {red:08x}"
            );
            assert_eq!(at(&first, 0, 0), 0, "rounded corner is not transparent");
            assert!(at(&first, 75, 60) >> 24 > 250, "ink coverage is missing");
            paint(0x20D030);
            for _ in 0..6 {
                crate::win32::pump_messages();
                gpu.draw(bounds, 1.0, &settings, None, &ink).unwrap();
                std::thread::sleep(Duration::from_millis(65));
            }
            let green = at(&gpu.test_pixels, 200, 60);
            eprintln!(
                "{material:?} after background change: {green:08x}; frames {before} → {}",
                gpu.frames
            );
            assert!(
                ((green >> 8) & 255) > ((green >> 16) & 255) + 70,
                "visible unchanged capsule did not follow green background: {green:08x}"
            );
            let frames = gpu.frames;
            for _ in 0..8 {
                crate::win32::pump_messages();
                gpu.draw(bounds, 1.0, &settings, None, &ink).unwrap();
                std::thread::sleep(Duration::from_millis(65));
            }
            eprintln!(
                "{material:?} stationary frame count: {frames} → {}",
                gpu.frames
            );
            assert!(
                gpu.frames <= frames + 2,
                "self-presentation keeps changing the captured background"
            );
            window.set_click_through(false);
            crate::win32::pump_messages();
            assert_eq!(
                WindowFromPoint(windows_sys::Win32::Foundation::POINT {
                    x: bounds.x + bounds.width / 2,
                    y: bounds.y + bounds.height / 2,
                }),
                window.test_input_handle(),
                "glass input companion did not receive hit tests"
            );
            window.set_click_through(true);
            // A temporary fallback must not leave the old glass surface or
            // lose the user's click-through preference when it recovers.
            drop(gpu);
            window.fallback_to_translucent("integration check");
            assert!(window.click_through());
            assert!(window.restore_gpu_material());
            let mut recovered = Glass::new(window.handle(), bounds).unwrap();
            for _ in 0..4 {
                recovered.draw(bounds, 1.0, &settings, None, &ink).unwrap();
                std::thread::sleep(Duration::from_millis(65));
            }
            assert!(recovered.frames > 0, "GPU did not recover after fallback");
        }
    }
}
