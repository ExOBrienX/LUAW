import { App, AppBootMode, AppInstallProps, AppSuspendMode, AppView, AppViewProps, Efb, RequiredProps, TVNode } from "@efb/efb-api";
import { FSComponent, VNode } from "@microsoft/msfs-sdk";

import "./RadarApp.scss";

/** Definida en build.js: la carpeta de la app dentro del paquete. */
declare const BASE_URL: string;

/** Servidor local de SimATC (ADR-073). */
const URL = "ws://127.0.0.1:47110/traffic";

/** Escalas del radar (NM hasta el arco exterior). */
const RANGES = [2, 5, 10, 20, 40, 80, 160];

/** TCAS: "proximate" a menos de 6 NM y 1200 ft; "TA" a menos de 3 NM y 900 ft (APROXIMADO, solo visual). */
const PROX_NM = 6, PROX_FT = 1200, TA_NM = 3, TA_FT = 900;

interface Own { lat: number; lon: number; hdg: number; alt: number; gs: number; gnd: boolean; }
interface Target {
  id: number; cs: string; type?: string; lat: number; lon: number; hdg: number; alt: number; gs: number;
  nm: number; brg: number; trend: number; gnd: boolean; parked: boolean; from?: string; to?: string;
}
interface Message { me?: Own; traffic: Target[]; route?: [number, number, string][]; }

/**
 * Radar de tráfico de SimATC en el EFB (ADR-073, rehecho como ND/TCAS en ADR-080): el avión del piloto abajo al centro,
 * arco de rumbo graduado, anillos de distancia, la ruta del plan en magenta y el tráfico con símbolos TCAS (altitud
 * relativa en cientos de pies, flecha si sube o baja, indicativo). Zoom con − y +. Ocupa toda la pantalla de la app.
 */
class RadarAppView extends AppView<RequiredProps<AppViewProps, "bus">> {
  private readonly hostRef = FSComponent.createRef<HTMLDivElement>();
  private readonly canvasRef = FSComponent.createRef<HTMLCanvasElement>();
  private readonly barRef = FSComponent.createRef<HTMLDivElement>();

  private socket: WebSocket | null = null;
  private retry: number | null = null;
  // Vigilancia (ADR-085): SimATC manda 2 veces por segundo; si en 6 s no llega nada con el socket "abierto" (conexión
  // medio caída al reiniciar SimATC o el simulador), se reconecta solo. Antes había que recompilar el paquete.
  private watchdog: number | null = null;
  private lastMessageAt = 0;
  private last: Message | null = null;
  private status = "Conectando con SimATC...";
  private rangeIndex = 2;
  private showParked = false;
  private showLabels = true;
  private rangeText: HTMLDivElement | null = null;
  private parkedBtn: HTMLDivElement | null = null;
  private labelBtn: HTMLDivElement | null = null;

  public onAfterRender(node: VNode): void {
    super.onAfterRender(node);
    const bar = this.barRef.instance;
    this.button(bar, "−", () => this.zoom(-1), "zoom");
    this.rangeText = this.button(bar, "", () => undefined, "range");
    this.button(bar, "+", () => this.zoom(1), "zoom");
    this.labelBtn = this.button(bar, "", () => { this.showLabels = !this.showLabels; this.refresh(); });
    this.parkedBtn = this.button(bar, "", () => { this.showParked = !this.showParked; this.refresh(); });
    this.refresh();
  }

  public onOpen(): void { this.connect(); }

  public onResume(): void {
    if (this.socket === null) this.connect();
    this.refresh();
  }

  public onClose(): void { this.disconnect(); }

  private button(parent: HTMLElement, text: string, onClick: () => void, extra = ""): HTMLDivElement {
    const b = document.createElement("div");
    b.className = "btn " + extra;
    b.textContent = text;
    b.addEventListener("click", onClick);
    parent.appendChild(b);
    return b;
  }

  private zoom(step: number): void {
    this.rangeIndex = Math.max(0, Math.min(RANGES.length - 1, this.rangeIndex + step));
    this.refresh();
  }

  private connect(): void {
    this.disconnect();
    try {
      const ws = new WebSocket(URL);
      this.socket = ws;
      ws.onopen = (): void => { this.status = "Conectado con SimATC"; this.refresh(); };
      ws.onmessage = (e: MessageEvent): void => {
        this.lastMessageAt = Date.now();
        try { this.last = JSON.parse(String(e.data)) as Message; } catch { return; }
        this.refresh();
      };
      ws.onclose = (): void => {
        this.socket = null;
        this.last = null;
        this.status = "Sin conexion con SimATC (esta abierto?). Reintentando...";
        this.refresh();
        this.retry = window.setTimeout(() => this.connect(), 3000);
      };
    } catch {
      this.retry = window.setTimeout(() => this.connect(), 3000);
    }
    this.lastMessageAt = Date.now();
    this.watchdog = window.setInterval(() => {
      if (Date.now() - this.lastMessageAt > 6000) {
        this.status = "Sin datos de SimATC. Reconectando...";
        this.last = null;
        this.refresh();
        this.connect();
      }
    }, 2000);
  }

  private disconnect(): void {
    if (this.retry !== null) window.clearTimeout(this.retry);
    this.retry = null;
    if (this.watchdog !== null) window.clearInterval(this.watchdog);
    this.watchdog = null;
    if (this.socket !== null) {
      this.socket.onclose = null;
      this.socket.close();
      this.socket = null;
    }
  }

  private refresh(): void {
    if (!this.canvasRef.getOrDefault()) return;
    const range = RANGES[this.rangeIndex];
    if (this.rangeText) this.rangeText.textContent = `${range} NM`;
    if (this.labelBtn) this.labelBtn.textContent = this.showLabels ? "Etiquetas: sí" : "Etiquetas: no";
    if (this.parkedBtn) this.parkedBtn.textContent = this.showParked ? "Estacionados: sí" : "Estacionados: no";
    this.draw(range);
  }

  private draw(range: number): void {
    const canvas = this.canvasRef.instance;
    const host = this.hostRef.getOrDefault();
    // El tamaño real de la app (en Coherent el canvas no toma el del CSS: se le da a mano).
    const w = Math.max(200, Math.round(host?.clientWidth || window.innerWidth || 800));
    const h = Math.max(200, Math.round(host?.clientHeight || window.innerHeight || 600));
    if (canvas.width !== w || canvas.height !== h) {
      canvas.width = w;
      canvas.height = h;
      canvas.style.width = `${w}px`;
      canvas.style.height = `${h}px`;
    }
    const g = canvas.getContext("2d");
    if (!g) return;
    g.fillStyle = "#000000";
    g.fillRect(0, 0, w, h);
    const cx = w / 2;
    const cy = h * 0.78;
    const radius = Math.min(w * 0.46, cy - 50);
    const pxPerNm = radius / range;
    const me = this.last?.me;
    const hdg = me?.hdg ?? 0;

    // Arco de rumbo graduado (cada 5°, número cada 30°) y anillos de distancia.
    g.strokeStyle = "#ffffff";
    g.lineWidth = 2;
    g.beginPath();
    g.arc(cx, cy, radius, -Math.PI / 2 - 1.1, -Math.PI / 2 + 1.1);
    g.stroke();
    g.font = "bold 18px monospace";
    g.textAlign = "center";
    g.fillStyle = "#ffffff";
    for (let d = 0; d < 360; d += 5) {
      const rel = ((d - hdg + 540) % 360) - 180;
      if (Math.abs(rel) > 63) continue;
      const a = (rel * Math.PI) / 180;
      const len = d % 10 === 0 ? 14 : 7;
      g.beginPath();
      g.moveTo(cx + Math.sin(a) * radius, cy - Math.cos(a) * radius);
      g.lineTo(cx + Math.sin(a) * (radius + len), cy - Math.cos(a) * (radius + len));
      g.stroke();
      if (d % 30 === 0) g.fillText(`${d / 10}`, cx + Math.sin(a) * (radius + 30), cy - Math.cos(a) * (radius + 30) + 6);
    }
    // Rumbo actual arriba.
    g.fillStyle = "#e6c200";
    g.fillText(`${Math.round(hdg).toString().padStart(3, "0")}`, cx, cy - radius - 50);
    g.strokeStyle = "#5f6b78";
    g.setLineDash([6, 8]);
    for (const f of [0.5]) {
      g.beginPath();
      g.arc(cx, cy, radius * f, -Math.PI / 2 - 1.1, -Math.PI / 2 + 1.1);
      g.stroke();
    }
    g.setLineDash([]);
    g.fillStyle = "#8b949e";
    g.font = "16px monospace";
    g.textAlign = "left";
    g.fillText(`${range / 2}`, cx + Math.sin(1.1) * radius * 0.5 + 6, cy - Math.cos(1.1) * radius * 0.5);
    g.fillText(`${range}`, cx + Math.sin(1.1) * radius + 6, cy - Math.cos(1.1) * radius);

    if (!me) {
      g.fillStyle = "#e6edf3";
      g.font = "22px sans-serif";
      g.textAlign = "center";
      g.fillText(this.status, cx, cy - radius / 2);
      return;
    }

    const cosLat = Math.cos((me.lat * Math.PI) / 180);
    const a0 = (-hdg * Math.PI) / 180;
    const toScreen = (lat: number, lon: number): [number, number] => {
      const dN = (lat - me.lat) * 60;
      let dLon = lon - me.lon;
      if (dLon > 180) dLon -= 360;
      if (dLon < -180) dLon += 360;
      const dE = dLon * 60 * cosLat;
      return [cx + (dE * Math.cos(a0) - dN * Math.sin(a0)) * pxPerNm, cy - (dE * Math.sin(a0) + dN * Math.cos(a0)) * pxPerNm];
    };

    // Recorte al área del radar.
    g.save();
    g.beginPath();
    g.arc(cx, cy, radius + 4, 0, Math.PI * 2);
    g.clip();

    // Ruta del plan en magenta, con los fijos.
    const route = this.last?.route;
    if (route && route.length > 1) {
      g.strokeStyle = "#ff3df2";
      g.lineWidth = 3;
      g.beginPath();
      route.forEach((p, i) => {
        const [x, y] = toScreen(p[0], p[1]);
        if (i === 0) g.moveTo(x, y); else g.lineTo(x, y);
      });
      g.stroke();
      g.font = "15px monospace";
      g.textAlign = "left";
      for (const p of route) {
        if (!p[2]) continue;
        const [x, y] = toScreen(p[0], p[1]);
        g.fillStyle = "#ff3df2";
        g.save();
        g.translate(x, y);
        g.rotate(Math.PI / 4);
        g.fillRect(-4, -4, 8, 8);
        g.restore();
        g.fillStyle = "#ffffff";
        g.fillText(p[2], x + 8, y - 6);
      }
    }

    // Tráfico con símbolos TCAS.
    for (const t of this.last?.traffic ?? []) {
      if (t.parked && !this.showParked) continue;
      if (t.nm > range * 1.2) continue;
      const [x, y] = toScreen(t.lat, t.lon);
      const relFt = t.alt - me.alt;
      const ta = !t.gnd && !me.gnd && t.nm < TA_NM && Math.abs(relFt) < TA_FT;
      const prox = !t.gnd && t.nm < PROX_NM && Math.abs(relFt) < PROX_FT;
      const color = ta ? "#ffb000" : t.gnd ? "#8b949e" : "#00e5ff";
      g.fillStyle = color;
      g.strokeStyle = color;
      g.lineWidth = 2;
      if (ta) {
        g.beginPath();
        g.arc(x, y, 8, 0, Math.PI * 2);
        g.fill();
      } else {
        g.beginPath();
        g.moveTo(x, y - 9);
        g.lineTo(x + 7, y);
        g.lineTo(x, y + 9);
        g.lineTo(x - 7, y);
        g.closePath();
        if (prox) g.fill(); else g.stroke();
      }
      if (t.parked && this.rangeIndex > 1) continue;
      // Altitud relativa en cientos de pies (+05 = 500 ft arriba) y flecha de tendencia.
      if (!t.gnd) {
        const hundreds = Math.round(Math.abs(relFt) / 100);
        const txt = `${relFt >= 0 ? "+" : "-"}${hundreds.toString().padStart(2, "0")}`;
        g.font = "bold 16px monospace";
        g.textAlign = "center";
        g.fillText(txt, x, relFt >= 0 ? y - 14 : y + 26);
        if (t.trend !== 0) {
          const ax = x + 14;
          g.beginPath();
          g.moveTo(ax, y - 8);
          g.lineTo(ax, y + 8);
          g.stroke();
          g.beginPath();
          if (t.trend > 0) { g.moveTo(ax - 4, y - 3); g.lineTo(ax, y - 9); g.lineTo(ax + 4, y - 3); }
          else { g.moveTo(ax - 4, y + 3); g.lineTo(ax, y + 9); g.lineTo(ax + 4, y + 3); }
          g.stroke();
        }
      }
      if (this.showLabels) {
        g.font = "14px monospace";
        g.textAlign = "left";
        g.fillStyle = "#c9d1d9";
        g.fillText(`${t.cs} ${t.gs}kt`, x + 22, y + 5);
      }
    }
    g.restore();

    // El piloto: símbolo amarillo fijo abajo al centro.
    g.strokeStyle = "#e6c200";
    g.lineWidth = 4;
    g.beginPath();
    g.moveTo(cx, cy - 18);
    g.lineTo(cx, cy + 14);
    g.moveTo(cx - 16, cy - 2);
    g.lineTo(cx + 16, cy - 2);
    g.moveTo(cx - 7, cy + 12);
    g.lineTo(cx + 7, cy + 12);
    g.stroke();

    // Datos propios abajo a la izquierda.
    g.fillStyle = "#00ff7f";
    g.font = "bold 18px monospace";
    g.textAlign = "left";
    g.fillText(`GS ${me.gs}`, 16, h - 44);
    g.fillText(me.gnd ? "EN TIERRA" : me.alt >= 18000 ? `FL${Math.round(me.alt / 100).toString().padStart(3, "0")}` : `${Math.round(me.alt / 100) * 100} FT`, 16, h - 18);
    const n = (this.last?.traffic ?? []).filter((t) => !t.parked && t.nm <= range).length;
    g.textAlign = "right";
    g.fillStyle = "#8b949e";
    g.fillText(`${n} TRAFICO${n === 1 ? "" : "S"}`, w - 16, h - 18);
  }

  public render(): VNode {
    return (
      <div ref={this.hostRef} class="simatc-radar">
        <canvas ref={this.canvasRef} class="scope"></canvas>
        <div ref={this.barRef} class="bar"></div>
      </div>
    );
  }
}

class RadarApp extends App {
  public get name(): string { return RadarApp.name; }

  public get icon(): string { return `${BASE_URL}/Assets/app-icon.svg`; }

  public BootMode = AppBootMode.COLD;

  public SuspendMode = AppSuspendMode.SLEEP;

  public async install(_props: AppInstallProps): Promise<void> {
    Efb.loadCss(`${BASE_URL}/RadarApp.css`);
    return Promise.resolve();
  }

  public render(): TVNode<RadarAppView> { return <RadarAppView bus={this.bus} />; }
}

Efb.use(RadarApp);
