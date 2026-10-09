using System.Globalization;
using System.Text;

namespace Ridgeline;

/// <summary>
/// A record of a war for watching afterwards. It keeps every mover's position each hour and who holds the ground every
/// three hours, along with the events, and writes them into one page. The page plays the war on the island map, with
/// a time slider, play and speed, territory shading, and hover for a unit's name. Wheel to zoom, drag to pan.
/// </summary>
public sealed class WarRecord
{
    readonly War _war;
    readonly List<int> _movers;
    readonly List<(double T, short[] XZ)> _frames = new();
    readonly List<(double T, string Owners)> _ground = new();

    public WarRecord(War war)
    {
        _war = war;
        _movers = war.Units.Where(u => u.IsMover).Select(u => u.Id).ToList();
    }

    /// <summary>Everyone's position now, in 25 m steps.</summary>
    public void Frame()
    {
        var xz = new short[_movers.Count * 2];
        for (int k = 0; k < _movers.Count; k++)
        {
            var u = _war.Units[_movers[k]];
            xz[k * 2] = (short)Math.Clamp(MathF.Round(u.X / 25f), short.MinValue, short.MaxValue);
            xz[k * 2 + 1] = (short)Math.Clamp(MathF.Round(u.Z / 25f), short.MinValue, short.MaxValue);
        }
        _frames.Add((_war.Time, xz));
    }

    /// <summary>Who holds each 1 km square: '.' sea, '0' nobody, '1'–'3' a side (lower case 'a'–'c' while contested).</summary>
    public void Ground()
    {
        var ctl = _war.Ctl;
        var sb = new StringBuilder(ctl.N * ctl.N);
        for (int c = 0; c < ctl.N * ctl.N; c++)
            sb.Append(!ctl.Land[c] ? '.' : ctl.Owner[c] < 0 ? '0' : ctl.Contested[c] ? (char)('a' + ctl.Owner[c]) : (char)('1' + ctl.Owner[c]));
        _ground.Add((_war.Time, sb.ToString()));
    }

    public void Write(string mapPng, string path, string report)
    {
        var war = _war;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=en><head><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(Esc(war.Isl.Name)).Append(": war ").Append(war.Seed).Append("</title><style>");
        sb.Append(":root{color-scheme:dark}body{margin:0;background:#16191d;color:#e6e3dc;font:13px/1.4 system-ui,sans-serif;display:flex;flex-direction:column;height:100vh}");
        sb.Append("#bar{display:flex;gap:10px;align-items:center;padding:8px 12px;background:#1f2328;flex-wrap:wrap}#bar input[type=range]{flex:1;min-width:200px}");
        sb.Append("button{background:#2c3238;color:#e6e3dc;border:1px solid #444b52;border-radius:4px;padding:4px 10px;cursor:pointer}#clock{font-variant-numeric:tabular-nums;min-width:120px}");
        sb.Append("#main{flex:1;display:flex;min-height:0}#view{flex:1;position:relative;overflow:hidden;cursor:grab;background:#9cc7e4}canvas{position:absolute;left:0;top:0}");
        sb.Append("#side{width:340px;overflow:auto;background:#1b1f23;padding:8px 10px;border-left:1px solid #2c3238}#side h3{margin:8px 0 4px;font-size:13px}.ev{padding:2px 0;border-bottom:1px solid #262b30}");
        sb.Append("#tip{position:absolute;pointer-events:none;background:#000c;color:#fff;padding:3px 6px;border-radius:3px;font-size:12px;display:none;white-space:nowrap}");
        sb.Append(".s0{color:#7f9be0}.s1{color:#e38a7a}.s2{color:#e8cf5a}pre{white-space:pre-wrap;font-size:11px}</style></head><body>");
        sb.Append("<div id=bar><button id=play>Play</button><button id=slow>&minus;</button><span id=speed>2 h/s</span><button id=fast>+</button>");
        sb.Append("<input id=t type=range min=0 value=0 step=1><span id=clock></span><label><input id=terr type=checkbox checked> territory</label></div>");
        sb.Append("<div id=main><div id=view><canvas id=c></canvas><div id=tip></div></div><div id=side><div id=score></div><h3>Events</h3><div id=events></div>");
        sb.Append("<h3>Report</h3><pre>").Append(Esc(report)).Append("</pre></div></div>");
        // Data.
        sb.Append("<script>const MAP='data:image/png;base64,").Append(Convert.ToBase64String(File.ReadAllBytes(mapPng))).Append("';\n");
        sb.Append("const EXT=").Append(war.Isl.Extent.ToString(inv)).Append(",GN=").Append(war.Ctl.N).Append(";\n");
        sb.Append("const SIDES=['").Append(string.Join("','", war.Sides.Select(s => s.Name))).Append("'];\n");
        sb.Append("const COL=['#3a5694','#94331f','#dbbd1f'];\n");
        sb.Append("const U=[");
        foreach (int id in _movers)
        {
            var u = war.Units[id];
            sb.Append('[').Append(u.Side).Append(',').Append((int)u.Echelon).Append(",\"").Append(Esc(u.Short)).Append("\",\"").Append(Esc(u.Name))
              .Append("\",").Append(u.People).Append(",\"").Append(u.Mob.ToString().ToLowerInvariant()).Append("\"],");
        }
        sb.Append("];\nconst F=[");
        foreach (var (t, xz) in _frames)
        {
            sb.Append('[').Append(((int)t).ToString(inv)).Append(",[");
            sb.Append(string.Join(',', xz)).Append("]],");
        }
        sb.Append("];\nconst G=[");
        foreach (var (t, g) in _ground) sb.Append('[').Append(((int)t).ToString(inv)).Append(",\"").Append(g).Append("\"],");
        // Fights where someone was hit: start, end, where, and how many were hit, for the rings on the map.
        sb.Append("];\nconst X=[");
        foreach (var f in war.Fights)
        {
            int hit = f.Killed.Sum() + f.Down.Sum() + f.Hurt.Sum();
            if (hit == 0) continue;
            sb.Append('[').Append(((int)f.Started).ToString(inv)).Append(',').Append(((int)(f.Over ? f.Ended : war.Time)).ToString(inv)).Append(',')
              .Append(((int)f.X).ToString(inv)).Append(',').Append(((int)f.Z).ToString(inv)).Append(',').Append(hit).Append("],");
        }
        sb.Append("];\nconst E=[");
        foreach (var (t, s, text, x, z) in war.Events)
            sb.Append('[').Append(((int)t).ToString(inv)).Append(',').Append(s).Append(",\"").Append(Esc(text)).Append("\",")
              .Append(((int)x).ToString(inv)).Append(',').Append(((int)z).ToString(inv)).Append("],");
        sb.Append("];\n");
        sb.Append(Viewer);
        sb.Append("</script></body></html>");
        File.WriteAllText(path, sb.ToString());
    }

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("\\", "\\\\");

    const string Viewer = """
const view=document.getElementById('view'),cv=document.getElementById('c'),cx=cv.getContext('2d'),tip=document.getElementById('tip');
const slider=document.getElementById('t'),clock=document.getElementById('clock');
const img=new Image();img.src=MAP;
let W=0,H=0,zoom=1,ox=0,oy=0,fi=0,playing=false,speed=2,acc=0,last=performance.now();
slider.max=F.length-1;
function px(x){return (x/EXT+0.5)*2048*zoom+ox}function py(z){return (z/EXT+0.5)*2048*zoom+oy}
function resize(){W=view.clientWidth;H=view.clientHeight;cv.width=W;cv.height=H;if(!zoom||zoom===1){zoom=Math.min(W,H)/2048;ox=(W-2048*zoom)/2;oy=(H-2048*zoom)/2}draw()}
addEventListener('resize',resize);
function when(t){const h=6+t/3600,d=1+Math.floor(h/24),hh=Math.floor(h%24),mm=Math.floor((h*60)%60);return 'Day '+d+', '+String(hh).padStart(2,'0')+':'+String(mm).padStart(2,'0')}
function ground(t){let g=null;for(const x of G){if(x[0]<=t)g=x;else break}return g}
function draw(){
  cx.fillStyle='#9cc7e4';cx.fillRect(0,0,W,H);
  if(img.complete)cx.drawImage(img,ox,oy,2048*zoom,2048*zoom);
  const f=F[fi];if(!f)return;const t=f[0];
  if(document.getElementById('terr').checked){const g=ground(t);if(g){const s=g[1],cell=EXT/GN;
    for(let i=0;i<s.length;i++){const ch=s[i];if(ch==='.'||ch==='0')continue;const contested=ch>='a';const side=contested?ch.charCodeAt(0)-97:ch.charCodeAt(0)-49;
      const x=(i%GN)*cell-EXT/2,z=Math.floor(i/GN)*cell-EXT/2;cx.fillStyle=COL[side]+(contested?'55':'33');
      cx.fillRect(px(x),py(z),cell/EXT*2048*zoom+0.5,cell/EXT*2048*zoom+0.5)}}}
  for(const x of X){if(x[0]>t||x[1]<t-1800)continue;const r=Math.max(6,Math.min(24,4+Math.sqrt(x[4])*2));
    cx.strokeStyle='#ff2a00';cx.lineWidth=2.5;cx.beginPath();cx.arc(px(x[2]),py(x[3]),r,0,7);cx.stroke()}
  const xz=f[1];
  for(let k=0;k<U.length;k++){const u=U[k],x=px(xz[k*2]*25),y=py(xz[k*2+1]*25);if(x<-10||y<-10||x>W+10||y>H+10)continue;
    const e=u[1],r=e>=4?6:e===3?4:2.6;cx.fillStyle='#111';cx.fillRect(x-r-1,y-r-1,2*r+2,2*r+2);cx.fillStyle=COL[u[0]];cx.fillRect(x-r,y-r,2*r,2*r)}
  clock.textContent=when(t);slider.value=fi;
  const counts=[0,0,0],tot=[0,0,0];const g=ground(t);if(g)for(const ch of g[1]){if(ch==='.')continue;for(let s=0;s<3;s++)tot[s]++;if(ch!=='0'){const side=ch>='a'?ch.charCodeAt(0)-97:ch.charCodeAt(0)-49;counts[side]++}}
  document.getElementById('score').innerHTML=SIDES.map((s,i)=>'<span class=s'+i+'><b>'+s+'</b> '+(tot[i]?Math.round(100*counts[i]/tot[i]):0)+'% of the land</span>').join(' · ');
  const ev=E.filter(e=>e[0]<=t).slice(-40).reverse();
  document.getElementById('events').innerHTML=ev.map(e=>'<div class="ev s'+e[1]+'">'+when(e[0])+': '+e[2]+'</div>').join('');
}
img.onload=resize;resize();
slider.oninput=()=>{fi=+slider.value;draw()};
document.getElementById('terr').onchange=draw;
document.getElementById('play').onclick=e=>{playing=!playing;e.target.textContent=playing?'Pause':'Play';last=performance.now()};
const speeds=[0.5,1,2,4,8,16];let si=2;function setSpeed(){speed=speeds[si];document.getElementById('speed').textContent=speed+' h/s'}
document.getElementById('slow').onclick=()=>{si=Math.max(0,si-1);setSpeed()};document.getElementById('fast').onclick=()=>{si=Math.min(speeds.length-1,si+1);setSpeed()};
function tick(now){if(playing){acc+=(now-last)/1000*speed;while(acc>=1&&fi<F.length-1){fi++;acc-=1}if(fi>=F.length-1){playing=false;document.getElementById('play').textContent='Play'}draw()}last=now;requestAnimationFrame(tick)}
requestAnimationFrame(tick);
view.addEventListener('wheel',e=>{e.preventDefault();const r=view.getBoundingClientRect(),mx=e.clientX-r.left,my=e.clientY-r.top,k=e.deltaY<0?1.2:1/1.2;ox=mx-(mx-ox)*k;oy=my-(my-oy)*k;zoom*=k;draw()},{passive:false});
let drag=null;view.addEventListener('mousedown',e=>{drag=[e.clientX-ox,e.clientY-oy]});addEventListener('mouseup',()=>drag=null);
view.addEventListener('mousemove',e=>{if(drag){ox=e.clientX-drag[0];oy=e.clientY-drag[1];draw();return}
  const r=view.getBoundingClientRect(),mx=e.clientX-r.left,my=e.clientY-r.top,f=F[fi];if(!f)return;let best=-1,bd=64;
  for(let k=0;k<U.length;k++){const dx=px(f[1][k*2]*25)-mx,dy=py(f[1][k*2+1]*25)-my,d=dx*dx+dy*dy;if(d<bd){bd=d;best=k}}
  if(best<0){tip.style.display='none';return}const u=U[best];tip.style.display='block';tip.style.left=(mx+12)+'px';tip.style.top=(my+8)+'px';
  tip.textContent=u[3].split(', ').slice(0,3).join(', ')+' — '+u[4]+' soldiers, '+u[5]});
view.addEventListener('mouseleave',()=>tip.style.display='none');
""";
}
