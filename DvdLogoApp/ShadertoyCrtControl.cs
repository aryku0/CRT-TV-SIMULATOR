using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;

namespace DvdLogoApp;

public sealed class ShadertoyCrtControl : OpenGlControlBase
{
    public static readonly StyledProperty<double> FresnelStrengthProperty =
        AvaloniaProperty.Register<ShadertoyCrtControl, double>(nameof(FresnelStrength), 0.58);

    public static readonly StyledProperty<double> NoiseStrengthProperty =
        AvaloniaProperty.Register<ShadertoyCrtControl, double>(nameof(NoiseStrength), 0.05);

    public static readonly StyledProperty<double> ScanlineStrengthProperty =
        AvaloniaProperty.Register<ShadertoyCrtControl, double>(nameof(ScanlineStrength), 0.42);

    public static readonly StyledProperty<double> GlitchStrengthProperty =
        AvaloniaProperty.Register<ShadertoyCrtControl, double>(nameof(GlitchStrength), 0.0);

    public static readonly StyledProperty<double> CurveStrengthProperty =
        AvaloniaProperty.Register<ShadertoyCrtControl, double>(nameof(CurveStrength), 0.55);

    public static readonly StyledProperty<double> VignetteStrengthProperty =
        AvaloniaProperty.Register<ShadertoyCrtControl, double>(nameof(VignetteStrength), 0.52);

    private const int GlArrayBuffer = 0x8892;
    private const int GlStaticDraw = 0x88E4;
    private const int GlFloat = 0x1406;
    private const int GlTriangles = 0x0004;
    private const int GlColorBufferBit = 0x00004000;
    private const int GlFramebuffer = 0x8D40;
    private const int GlBlend = 0x0BE2;
    private const int GlSrcAlpha = 0x0302;
    private const int GlOneMinusSrcAlpha = 0x0303;
    private const int GlVertexShader = 0x8B31;
    private const int GlFragmentShader = 0x8B30;
    private const int GlCompileStatus = 0x8B81;
    private const int GlLinkStatus = 0x8B82;

    private static readonly float[] FullscreenTriangle =
    [
        -1f, -1f,
        3f, -1f,
        -1f, 3f
    ];

    private readonly DispatcherTimer renderTimer;
    private readonly DateTime startTime = DateTime.UtcNow;
    private GlApi? api;
    private uint program;
    private uint vertexBuffer;
    private int frameIndex;
    private int aPositionLocation = -1;
    private int iResolutionLocation = -1;
    private int iTimeLocation = -1;
    private int iFrameLocation = -1;
    private int analogLocation = -1;
    private int digitalLocation = -1;
    private int crtLocation = -1;
    private int noiseLocation = -1;
    private int scanlineLocation = -1;
    private int fresnelLocation = -1;
    private int curveLocation = -1;
    private int vignetteLocation = -1;

    public double FresnelStrength
    {
        get => GetValue(FresnelStrengthProperty);
        set => SetValue(FresnelStrengthProperty, value);
    }

    public double NoiseStrength
    {
        get => GetValue(NoiseStrengthProperty);
        set => SetValue(NoiseStrengthProperty, value);
    }

    public double ScanlineStrength
    {
        get => GetValue(ScanlineStrengthProperty);
        set => SetValue(ScanlineStrengthProperty, value);
    }

    public double GlitchStrength
    {
        get => GetValue(GlitchStrengthProperty);
        set => SetValue(GlitchStrengthProperty, value);
    }

    public double CurveStrength
    {
        get => GetValue(CurveStrengthProperty);
        set => SetValue(CurveStrengthProperty, value);
    }

    public double VignetteStrength
    {
        get => GetValue(VignetteStrengthProperty);
        set => SetValue(VignetteStrengthProperty, value);
    }

    public ShadertoyCrtControl()
    {
        IsHitTestVisible = false;

        renderTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        renderTimer.Tick += (_, _) => RequestNextFrameRendering();

        AttachedToVisualTree += (_, _) => renderTimer.Start();
        DetachedFromVisualTree += (_, _) => renderTimer.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == FresnelStrengthProperty
            || change.Property == NoiseStrengthProperty
            || change.Property == ScanlineStrengthProperty
            || change.Property == GlitchStrengthProperty
            || change.Property == CurveStrengthProperty
            || change.Property == VignetteStrengthProperty)
        {
            RequestNextFrameRendering();
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        api = new GlApi(gl);
        program = BuildProgram(api, VertexShaderSource, FragmentShaderSource);
        vertexBuffer = api.CreateVertexBuffer(FullscreenTriangle);

        aPositionLocation = api.GetAttribLocation(program, "aPosition");
        iResolutionLocation = api.GetUniformLocation(program, "iResolution");
        iTimeLocation = api.GetUniformLocation(program, "iTime");
        iFrameLocation = api.GetUniformLocation(program, "iFrame");
        analogLocation = api.GetUniformLocation(program, "uAnalogAmount");
        digitalLocation = api.GetUniformLocation(program, "uDigitalAmount");
        crtLocation = api.GetUniformLocation(program, "uCrtAmount");
        noiseLocation = api.GetUniformLocation(program, "uNoiseAmount");
        scanlineLocation = api.GetUniformLocation(program, "uScanlineAmount");
        fresnelLocation = api.GetUniformLocation(program, "uFresnelAmount");
        curveLocation = api.GetUniformLocation(program, "uCurveAmount");
        vignetteLocation = api.GetUniformLocation(program, "uVignetteAmount");
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        var glApi = api;

        if (glApi is null || program == 0 || vertexBuffer == 0 || aPositionLocation < 0)
        {
            return;
        }

        var renderScale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var width = Math.Max(1, (int)Math.Round(Bounds.Width * renderScale));
        var height = Math.Max(1, (int)Math.Round(Bounds.Height * renderScale));
        var time = (float)(DateTime.UtcNow - startTime).TotalSeconds;

        glApi.BindFramebuffer(GlFramebuffer, (uint)fb);
        glApi.Viewport(0, 0, width, height);
        glApi.Enable(GlBlend);
        glApi.BlendFunc(GlSrcAlpha, GlOneMinusSrcAlpha);
        glApi.ClearColor(0, 0, 0, 0);
        glApi.Clear(GlColorBufferBit);
        glApi.UseProgram(program);
        glApi.Uniform3f(iResolutionLocation, width, height, 1);
        glApi.Uniform1f(iTimeLocation, time);
        glApi.Uniform1f(iFrameLocation, frameIndex++);
        glApi.Uniform1f(analogLocation, Clamp01(GlitchStrength));
        glApi.Uniform1f(digitalLocation, Clamp01(GlitchStrength));
        glApi.Uniform1f(crtLocation, Clamp01(CurveStrength));
        glApi.Uniform1f(noiseLocation, Clamp01(NoiseStrength));
        glApi.Uniform1f(scanlineLocation, Clamp01(ScanlineStrength));
        glApi.Uniform1f(fresnelLocation, Clamp01(FresnelStrength));
        glApi.Uniform1f(curveLocation, Clamp01(CurveStrength));
        glApi.Uniform1f(vignetteLocation, Clamp01(VignetteStrength));
        glApi.BindBuffer(GlArrayBuffer, vertexBuffer);
        glApi.EnableVertexAttribArray((uint)aPositionLocation);
        glApi.VertexAttribPointer((uint)aPositionLocation, 2, GlFloat, 0, 0, IntPtr.Zero);
        glApi.DrawArrays(GlTriangles, 0, 3);
        glApi.DisableVertexAttribArray((uint)aPositionLocation);
        glApi.BindBuffer(GlArrayBuffer, 0);
        glApi.UseProgram(0);
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        var glApi = api;

        if (glApi is null)
        {
            return;
        }

        if (vertexBuffer != 0)
        {
            glApi.DeleteBuffers(1, ref vertexBuffer);
            vertexBuffer = 0;
        }

        if (program != 0)
        {
            glApi.DeleteProgram(program);
            program = 0;
        }

        api = null;
    }

    private static uint BuildProgram(GlApi gl, string vertexSource, string fragmentSource)
    {
        var vertexShader = CompileShader(gl, GlVertexShader, vertexSource);
        var fragmentShader = CompileShader(gl, GlFragmentShader, fragmentSource);
        var shaderProgram = gl.CreateProgram();

        gl.AttachShader(shaderProgram, vertexShader);
        gl.AttachShader(shaderProgram, fragmentShader);
        gl.LinkProgram(shaderProgram);
        gl.GetProgramiv(shaderProgram, GlLinkStatus, out var linked);
        gl.DeleteShader(vertexShader);
        gl.DeleteShader(fragmentShader);

        if (linked == 0)
        {
            var log = gl.GetProgramInfoLog(shaderProgram);
            gl.DeleteProgram(shaderProgram);
            throw new InvalidOperationException($"CRT shader link failed: {log}");
        }

        return shaderProgram;
    }

    private static uint CompileShader(GlApi gl, int shaderType, string source)
    {
        var shader = gl.CreateShader(shaderType);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShaderiv(shader, GlCompileStatus, out var compiled);

        if (compiled == 0)
        {
            var log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new InvalidOperationException($"CRT shader compile failed: {log}");
        }

        return shader;
    }

    private static double Clamp01(double value)
    {
        return Math.Clamp(value, 0, 1);
    }

    private const string VertexShaderSource = """
#ifdef GL_ES
precision mediump float;
#endif
attribute vec2 aPosition;

void main()
{
    gl_Position = vec4(aPosition, 0.0, 1.0);
}
""";

    private const string FragmentShaderSource = """
#ifdef GL_ES
precision mediump float;
#endif

uniform vec3 iResolution;
uniform float iTime;
uniform float iFrame;
uniform float uAnalogAmount;
uniform float uDigitalAmount;
uniform float uCrtAmount;
uniform float uNoiseAmount;
uniform float uScanlineAmount;
uniform float uFresnelAmount;
uniform float uCurveAmount;
uniform float uVignetteAmount;

#define DURATION 5.0
#define AMT 0.5
#define SS(a, b, x) (smoothstep(a, b, x) * smoothstep(b, a, x))

vec3 hash33(vec3 p)
{
    p = fract(p * vec3(0.1031, 0.11369, 0.13787));
    p += dot(p, p.yxz + 19.19);
    return -1.0 + 2.0 * fract(vec3((p.x + p.y) * p.z, (p.x + p.z) * p.y, (p.y + p.z) * p.x));
}

float gnoise(vec3 x)
{
    vec3 p = floor(x);
    vec3 w = fract(x);
    vec3 u = w * w * w * (w * (w * 6.0 - 15.0) + 10.0);

    vec3 ga = hash33(p + vec3(0.0, 0.0, 0.0));
    vec3 gb = hash33(p + vec3(1.0, 0.0, 0.0));
    vec3 gc = hash33(p + vec3(0.0, 1.0, 0.0));
    vec3 gd = hash33(p + vec3(1.0, 1.0, 0.0));
    vec3 ge = hash33(p + vec3(0.0, 0.0, 1.0));
    vec3 gf = hash33(p + vec3(1.0, 0.0, 1.0));
    vec3 gg = hash33(p + vec3(0.0, 1.0, 1.0));
    vec3 gh = hash33(p + vec3(1.0, 1.0, 1.0));

    float va = dot(ga, w - vec3(0.0, 0.0, 0.0));
    float vb = dot(gb, w - vec3(1.0, 0.0, 0.0));
    float vc = dot(gc, w - vec3(0.0, 1.0, 0.0));
    float vd = dot(gd, w - vec3(1.0, 1.0, 0.0));
    float ve = dot(ge, w - vec3(0.0, 0.0, 1.0));
    float vf = dot(gf, w - vec3(1.0, 0.0, 1.0));
    float vg = dot(gg, w - vec3(0.0, 1.0, 1.0));
    float vh = dot(gh, w - vec3(1.0, 1.0, 1.0));

    float n = va + u.x * (vb - va)
        + u.y * (vc - va)
        + u.z * (ve - va)
        + u.x * u.y * (va - vb - vc + vd)
        + u.y * u.z * (va - vc - ve + vg)
        + u.z * u.x * (va - vb - ve + vf)
        + u.x * u.y * u.z * (-va + vb + vc - vd + ve - vf - vg + vh);

    return 2.0 * n;
}

float gnoise01(vec3 x)
{
    return 0.5 + 0.5 * gnoise(x);
}

vec2 crt(vec2 uv, float amount)
{
    float tht = atan(uv.y, uv.x);
    float r = length(uv);
    r /= (1.0 - (0.1 * amount * r * r));
    uv.x = r * cos(tht);
    uv.y = r * sin(tht);
    return 0.5 * (uv + 1.0);
}

float fresnelEdge(vec2 uv)
{
    vec2 edge = min(uv, 1.0 - uv);
    float d = clamp(min(edge.x, edge.y) * 5.0, 0.0, 1.0);
    float viewTerm = 1.0 - d;
    float f0 = 0.035;
    float f = f0 + (1.0 - f0) * pow(viewTerm, 5.0);
    return clamp((f - f0) / (1.0 - f0), 0.0, 1.0);
}

float roundedRectSdf(vec2 point, vec2 halfSize, float radius)
{
    vec2 q = abs(point) - halfSize + vec2(radius);
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord)
{
    vec2 uv = fragCoord / iResolution.xy;
    float t = iTime;
    float curve = clamp(uCurveAmount, 0.0, 1.0);
    float maskInset = 7.0 + 20.0 * curve;
    float maskRadius = 18.0 + min(iResolution.x, iResolution.y) * (0.11 * curve);
    vec2 maskPoint = (uv - 0.5) * iResolution.xy;
    vec2 maskHalfSize = iResolution.xy * 0.5 - vec2(maskInset);
    float maskSdf = roundedRectSdf(maskPoint, maskHalfSize, maskRadius);
    float outsideMask = smoothstep(-1.25, 1.25, maskSdf);

    if (outsideMask > 0.995)
    {
        fragColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    float glitchTrigger = SS(DURATION * 0.001, DURATION * AMT, mod(t, DURATION));
    float glitchAmount = glitchTrigger * max(uAnalogAmount, uDigitalAmount);
    vec2 crtUv = crt(uv * 2.0 - 1.0, curve);

    if (crtUv.x < 0.0 || crtUv.x > 1.0 || crtUv.y < 0.0 || crtUv.y > 1.0)
    {
        fragColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    float y = crtUv.y * iResolution.y;
    float distortion = gnoise(vec3(0.0, y * 0.01, t * 500.0)) * (glitchAmount * 4.0 + 0.1);
    distortion *= gnoise(vec3(0.0, y * 0.02, t * 250.0)) * (glitchAmount * 2.0 + 0.025);
    distortion += smoothstep(0.999, 1.0, sin((crtUv.y + t * 1.6) * 2.0)) * 0.02 * uAnalogAmount;
    distortion -= smoothstep(0.999, 1.0, sin((crtUv.y + t) * 2.0)) * 0.02 * uAnalogAmount;

    float line = 0.5 + 0.5 * sin(4.0 * t + crtUv.y * iResolution.y * 1.75);
    float scan = line * uScanlineAmount;
    float noise = hash33(vec3(fragCoord.xy, mod(iFrame, 1000.0))).r * uNoiseAmount;

    float bt = floor(t * 30.0) * 300.0;
    float blockGlitch = 0.2 + 0.9 * glitchAmount;
    float blockNoiseX = step(gnoise01(vec3(0.0, crtUv.x * 3.0, bt)), blockGlitch);
    float blockNoiseY = step(gnoise01(vec3(0.0, crtUv.y * 4.0, bt)), blockGlitch);
    float block = blockNoiseX * blockNoiseY * uDigitalAmount;
    float band = smoothstep(0.992, 1.0, sin((crtUv.y + t * 2.4 + distortion) * 22.0)) * uAnalogAmount;

    float vig = 8.0 * crtUv.x * crtUv.y * (1.0 - crtUv.x) * (1.0 - crtUv.y);
    float vignette = (1.0 - clamp(pow(vig, 0.25), 0.0, 1.0)) * uVignetteAmount;
    float edge = fresnelEdge(crtUv) * uFresnelAmount;
    float borderWidth = 14.0 + 42.0 * curve;
    float curvedBorder = 1.0 - smoothstep(0.0, borderWidth, -maskSdf);
    float maskBorder = max(outsideMask, curvedBorder * clamp(0.45 + curve, 0.0, 1.0));

    vec3 colour = vec3(0.0);
    colour += vec3(0.75, 0.92, 1.0) * edge;
    colour += vec3(1.0) * max(noise, band * 0.25);
    colour += vec3(0.0, 0.0, 0.0) * scan;
    colour += vec3(0.95, 0.08, 0.05) * block * 0.45;
    colour += vec3(0.05, 0.45, 1.0) * block * 0.35;

    float alpha = edge * 0.42
        + scan * 0.18
        + abs(noise) * 0.32
        + block * 0.22
        + band * 0.12
        + vignette * 0.38
        + maskBorder;

    vec3 darken = vec3(0.0);
    colour = mix(colour, darken, clamp(scan * 0.35 + vignette * 0.65 + maskBorder, 0.0, 1.0));
    fragColor = vec4(colour, clamp(alpha, 0.0, 1.0));
}

void main()
{
    vec4 colour;
    mainImage(colour, gl_FragCoord.xy);
    gl_FragColor = colour;
}
""";

    private sealed class GlApi
    {
        private readonly GlCreateShader createShader;
        private readonly GlShaderSource shaderSource;
        private readonly GlCompileShader compileShader;
        private readonly GlGetShaderiv getShaderiv;
        private readonly GlGetShaderInfoLog getShaderInfoLog;
        private readonly GlDeleteShader deleteShader;
        private readonly GlCreateProgram createProgram;
        private readonly GlAttachShader attachShader;
        private readonly GlLinkProgram linkProgram;
        private readonly GlGetProgramiv getProgramiv;
        private readonly GlGetProgramInfoLog getProgramInfoLog;
        private readonly GlDeleteProgram deleteProgram;
        private readonly GlUseProgram useProgram;
        private readonly GlGetAttribLocation getAttribLocation;
        private readonly GlGetUniformLocation getUniformLocation;
        private readonly GlUniform1f uniform1f;
        private readonly GlUniform3f uniform3f;
        private readonly GlGenBuffers genBuffers;
        private readonly GlBindBuffer bindBuffer;
        private readonly GlBufferData bufferData;
        private readonly GlDeleteBuffers deleteBuffers;
        private readonly GlEnableVertexAttribArray enableVertexAttribArray;
        private readonly GlDisableVertexAttribArray disableVertexAttribArray;
        private readonly GlVertexAttribPointer vertexAttribPointer;
        private readonly GlDrawArrays drawArrays;
        private readonly GlBindFramebuffer bindFramebuffer;
        private readonly GlViewport viewport;
        private readonly GlClearColor clearColor;
        private readonly GlClear clear;
        private readonly GlEnable enable;
        private readonly GlBlendFunc blendFunc;

        public GlApi(GlInterface gl)
        {
            createShader = Load<GlCreateShader>(gl, "glCreateShader");
            shaderSource = Load<GlShaderSource>(gl, "glShaderSource");
            compileShader = Load<GlCompileShader>(gl, "glCompileShader");
            getShaderiv = Load<GlGetShaderiv>(gl, "glGetShaderiv");
            getShaderInfoLog = Load<GlGetShaderInfoLog>(gl, "glGetShaderInfoLog");
            deleteShader = Load<GlDeleteShader>(gl, "glDeleteShader");
            createProgram = Load<GlCreateProgram>(gl, "glCreateProgram");
            attachShader = Load<GlAttachShader>(gl, "glAttachShader");
            linkProgram = Load<GlLinkProgram>(gl, "glLinkProgram");
            getProgramiv = Load<GlGetProgramiv>(gl, "glGetProgramiv");
            getProgramInfoLog = Load<GlGetProgramInfoLog>(gl, "glGetProgramInfoLog");
            deleteProgram = Load<GlDeleteProgram>(gl, "glDeleteProgram");
            useProgram = Load<GlUseProgram>(gl, "glUseProgram");
            getAttribLocation = Load<GlGetAttribLocation>(gl, "glGetAttribLocation");
            getUniformLocation = Load<GlGetUniformLocation>(gl, "glGetUniformLocation");
            uniform1f = Load<GlUniform1f>(gl, "glUniform1f");
            uniform3f = Load<GlUniform3f>(gl, "glUniform3f");
            genBuffers = Load<GlGenBuffers>(gl, "glGenBuffers");
            bindBuffer = Load<GlBindBuffer>(gl, "glBindBuffer");
            bufferData = Load<GlBufferData>(gl, "glBufferData");
            deleteBuffers = Load<GlDeleteBuffers>(gl, "glDeleteBuffers");
            enableVertexAttribArray = Load<GlEnableVertexAttribArray>(gl, "glEnableVertexAttribArray");
            disableVertexAttribArray = Load<GlDisableVertexAttribArray>(gl, "glDisableVertexAttribArray");
            vertexAttribPointer = Load<GlVertexAttribPointer>(gl, "glVertexAttribPointer");
            drawArrays = Load<GlDrawArrays>(gl, "glDrawArrays");
            bindFramebuffer = Load<GlBindFramebuffer>(gl, "glBindFramebuffer");
            viewport = Load<GlViewport>(gl, "glViewport");
            clearColor = Load<GlClearColor>(gl, "glClearColor");
            clear = Load<GlClear>(gl, "glClear");
            enable = Load<GlEnable>(gl, "glEnable");
            blendFunc = Load<GlBlendFunc>(gl, "glBlendFunc");
        }

        public uint CreateShader(int type) => createShader(type);
        public void CompileShader(uint shader) => compileShader(shader);
        public void GetShaderiv(uint shader, int pname, out int value) => getShaderiv(shader, pname, out value);
        public void DeleteShader(uint shader) => deleteShader(shader);
        public uint CreateProgram() => createProgram();
        public void AttachShader(uint shaderProgram, uint shader) => attachShader(shaderProgram, shader);
        public void LinkProgram(uint shaderProgram) => linkProgram(shaderProgram);
        public void GetProgramiv(uint shaderProgram, int pname, out int value) => getProgramiv(shaderProgram, pname, out value);
        public void DeleteProgram(uint shaderProgram) => deleteProgram(shaderProgram);
        public void UseProgram(uint shaderProgram) => useProgram(shaderProgram);
        public int GetAttribLocation(uint shaderProgram, string name) => getAttribLocation(shaderProgram, name);
        public int GetUniformLocation(uint shaderProgram, string name) => getUniformLocation(shaderProgram, name);
        public void Uniform1f(int location, double value) => uniform1f(location, (float)value);
        public void Uniform3f(int location, double x, double y, double z) => uniform3f(location, (float)x, (float)y, (float)z);
        public void BindBuffer(int target, uint buffer) => bindBuffer(target, buffer);
        public void EnableVertexAttribArray(uint index) => enableVertexAttribArray(index);
        public void DisableVertexAttribArray(uint index) => disableVertexAttribArray(index);
        public void VertexAttribPointer(uint index, int size, int type, byte normalised, int stride, IntPtr pointer) =>
            vertexAttribPointer(index, size, type, normalised, stride, pointer);
        public void DrawArrays(int mode, int first, int count) => drawArrays(mode, first, count);
        public void BindFramebuffer(int target, uint framebuffer) => bindFramebuffer(target, framebuffer);
        public void Viewport(int x, int y, int width, int height) => viewport(x, y, width, height);
        public void ClearColor(float r, float g, float b, float a) => clearColor(r, g, b, a);
        public void Clear(int mask) => clear(mask);
        public void Enable(int cap) => enable(cap);
        public void BlendFunc(int source, int destination) => blendFunc(source, destination);

        public uint CreateVertexBuffer(float[] vertices)
        {
            genBuffers(1, out var buffer);
            bindBuffer(GlArrayBuffer, buffer);
            bufferData(GlArrayBuffer, new IntPtr(vertices.Length * sizeof(float)), vertices, GlStaticDraw);
            bindBuffer(GlArrayBuffer, 0);
            return buffer;
        }

        public void DeleteBuffers(int count, ref uint buffer) => deleteBuffers(count, ref buffer);

        public void ShaderSource(uint shader, string source)
        {
            var sourceBytes = System.Text.Encoding.UTF8.GetBytes(source);
            var sourcePointer = Marshal.AllocHGlobal(sourceBytes.Length + 1);
            var sourcePointers = Marshal.AllocHGlobal(IntPtr.Size);
            var lengthPointer = Marshal.AllocHGlobal(sizeof(int));

            try
            {
                Marshal.Copy(sourceBytes, 0, sourcePointer, sourceBytes.Length);
                Marshal.WriteByte(sourcePointer, sourceBytes.Length, 0);
                Marshal.WriteIntPtr(sourcePointers, sourcePointer);
                Marshal.WriteInt32(lengthPointer, sourceBytes.Length);
                shaderSource(shader, 1, sourcePointers, lengthPointer);
            }
            finally
            {
                Marshal.FreeHGlobal(lengthPointer);
                Marshal.FreeHGlobal(sourcePointers);
                Marshal.FreeHGlobal(sourcePointer);
            }
        }

        public string GetShaderInfoLog(uint shader)
        {
            return ReadInfoLog((length, buffer, outLength) => getShaderInfoLog(shader, length, out outLength, buffer));
        }

        public string GetProgramInfoLog(uint shaderProgram)
        {
            return ReadInfoLog((length, buffer, outLength) => getProgramInfoLog(shaderProgram, length, out outLength, buffer));
        }

        private static string ReadInfoLog(Action<int, IntPtr, int> read)
        {
            const int BufferSize = 4096;
            var buffer = Marshal.AllocHGlobal(BufferSize);

            try
            {
                read(BufferSize, buffer, 0);
                return Marshal.PtrToStringAnsi(buffer) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static T Load<T>(GlInterface gl, string name)
            where T : Delegate
        {
            var pointer = gl.GetProcAddress(name);

            if (pointer == IntPtr.Zero)
            {
                throw new InvalidOperationException($"OpenGL function is unavailable: {name}");
            }

            return Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint GlCreateShader(int type);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlShaderSource(uint shader, int count, IntPtr strings, IntPtr lengths);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlCompileShader(uint shader);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlGetShaderiv(uint shader, int pname, out int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlGetShaderInfoLog(uint shader, int maxLength, out int length, IntPtr infoLog);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlDeleteShader(uint shader);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint GlCreateProgram();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlAttachShader(uint shaderProgram, uint shader);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlLinkProgram(uint shaderProgram);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlGetProgramiv(uint shaderProgram, int pname, out int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlGetProgramInfoLog(uint shaderProgram, int maxLength, out int length, IntPtr infoLog);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlDeleteProgram(uint shaderProgram);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlUseProgram(uint shaderProgram);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GlGetAttribLocation(uint shaderProgram, [MarshalAs(UnmanagedType.LPStr)] string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GlGetUniformLocation(uint shaderProgram, [MarshalAs(UnmanagedType.LPStr)] string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlUniform1f(int location, float value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlUniform3f(int location, float x, float y, float z);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlGenBuffers(int count, out uint buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlBindBuffer(int target, uint buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlBufferData(int target, IntPtr size, float[] data, int usage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlDeleteBuffers(int count, ref uint buffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlEnableVertexAttribArray(uint index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlDisableVertexAttribArray(uint index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlVertexAttribPointer(uint index, int size, int type, byte normalised, int stride, IntPtr pointer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlDrawArrays(int mode, int first, int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlBindFramebuffer(int target, uint framebuffer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlViewport(int x, int y, int width, int height);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlClearColor(float r, float g, float b, float a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlClear(int mask);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlEnable(int cap);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void GlBlendFunc(int source, int destination);
    }
}
