"""Run with: uv run --no-project python Autom8er/tests/FarmEnclosureHelper.check.py

Compiles the real FarmEnclosureHelper.cs against small game substitutes and checks
fence and path layouts. Needs the .NET 10 SDK, but no game process.
"""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
helper = (root / "code/FarmEnclosureHelper.cs").read_text()

stubs = r'''
namespace UnityEngine {
 public struct Vector2 { public float x,y; }
 public static class Mathf { public static int Abs(int n)=>System.Math.Abs(n); public static int Clamp(int n,int a,int b)=>System.Math.Clamp(n,a,b); public static int Max(int a,int b)=>System.Math.Max(a,b); }
 public static class Time { public static int frameCount; }
}
public class Chest { public int xPos,yPos; }
public class TileObjectConnect { public bool isFence=true; }
public class TileOnOff { public bool isGate=true; }
public class TileObject { public TileObjectConnect tileObjectConnect; public TileOnOff tileOnOff; }
public class TileTypes { public bool isPath; }
public class WorldManager {
 public static WorldManager Instance=new WorldManager();
 public int[,] onTileMap=new int[100,100],tileTypeMap=new int[100,100],onTileStatusMap=new int[100,100];
 // Game gate prefabs carry both flags: TileObjectConnect.isFence and OnOffTile.isGate.
 public TileObject[] allObjects={new TileObject{tileObjectConnect=new TileObjectConnect()},new TileObject{tileObjectConnect=new TileObjectConnect(),tileOnOff=new TileOnOff()}};
 public TileTypes[] tileTypes={new TileTypes(),new TileTypes{isPath=true},new TileTypes()};
 public UnityEngine.Vector2 findMultiTileObjectPos(int x,int y)=>new UnityEngine.Vector2{x=x,y=y};
}
namespace Autom8er { public class Plugin { public static int AUTO_FARMER_RANGE_TILES=10; public static int ConveyorTileType=2; } public static class VacuumCrateHelper { internal const int MAX_LOGGING_ROW_LENGTH=24; } }
namespace Cold { public class Plugin { public static int AUTO_FARMER_RANGE_TILES=10; public static int ConveyorTileType=2; } public static class VacuumCrateHelper { internal const int MAX_LOGGING_ROW_LENGTH=24; } }
'''

program = r'''
static class Program {
 static WorldManager W=>WorldManager.Instance;
 static List<(int x,int y)> inside=new List<(int,int)>(),outside=new List<(int,int)>();
 static Chest crate;
 static void Clear(){for(int x=0;x<100;x++)for(int y=0;y<100;y++){W.onTileMap[x,y]=-1;W.tileTypeMap[x,y]=0;W.onTileStatusMap[x,y]=0;}inside.Clear();outside.Clear();crate=null;UnityEngine.Time.frameCount++;}
 static void Set(int x,int y,char c){
  if(x<0||y<0||x>=100||y>=100)return;
  switch(c){case 'F':W.onTileMap[x,y]=0;break;case 'G':W.onTileMap[x,y]=1;break;case 'P':W.tileTypeMap[x,y]=1;break;case 'C':W.onTileMap[x,y]=-1;W.tileTypeMap[x,y]=2;break;case 'T':W.onTileMap[x,y]=5;break;
   case 'X':W.onTileMap[x,y]=412;crate=new Chest{xPos=x,yPos=y};break;case 'i':inside.Add((x,y));break;case 'o':outside.Add((x,y));break;}
 }
 static void Draw(int ox,int oy,params string[] rows){for(int r=0;r<rows.Length;r++)for(int c=0;c<rows[r].Length;c++)Set(ox+c,oy+r,rows[r][c]);}
 static void Rect(int x0,int y0,int x1,int y1,char c){for(int x=x0;x<=x1;x++){Set(x,y0,c);Set(x,y1,c);}for(int y=y0;y<=y1;y++){Set(x0,y,c);Set(x1,y,c);}}
 static void Expect(string label){
  UnityEngine.Time.frameCount++;
  // Only tiles inside the square range say anything about the enclosure.
  Predicate<(int x,int y)> far=t=>Math.Abs(t.x-crate.xPos)>10||Math.Abs(t.y-crate.yPos)>10;
  inside.RemoveAll(far);outside.RemoveAll(far);
  if(inside.Count==0)throw new Exception(label+": no inside tiles in range");
  foreach(var t in inside)if(!FarmEnclosureHelper.IsInFarmArea(crate,t.x,t.y))throw new Exception(label+": inside tile "+t+" excluded");
  foreach(var t in outside)if(FarmEnclosureHelper.IsInFarmArea(crate,t.x,t.y))throw new Exception(label+": outside tile "+t+" farmed");
  Console.WriteLine("PASS: "+label+" ("+inside.Count+" in, "+outside.Count+" out)");
 }
 static void Main(){
  Clear();Draw(40,40,
   "FFFFFFFFFFFF",
   "FFFFFFFFFFFF",
   "FFXiiiiiiiFF",
   "FFFFFFFFFFFF",
   "FFFFFFFFFFFF",
   "oooooooooooo");
  Expect("closed one-wide strip between two-thick fences");

  Clear();Draw(40,40,
   "PPPPPPPPPP",
   "FFFFFFFFFF",
   "FXiiiiiiiF",
   "FiiiiiiiiF",
   "FFFFFFFFFF",
   "PPPPPPPPPP",
   "oooooooooo");
  Expect("closed two-wide strip walled by fence and path");

  // A 70 x 70 field is larger than the scan window. The crate sits two tiles inside the west fence.
  foreach(bool conveyorGap in new[]{false,true}){
   Clear();Rect(10,10,79,79,'F');if(conveyorGap){Set(10,41,'C');Set(10,42,'C');}
   Set(12,40,'X');for(int y=31;y<=49;y++){for(int x=2;x<=9;x++)Set(x,y,'o');for(int x=11;x<=22;x++)if(x!=12||y!=40)Set(x,y,'i');}
   Expect(conveyorGap?"oversized closed field with a two-tile conveyor gap":"oversized closed field");
  }
  // Logging rows reach 24 tiles, past the farm square, and still stop at a fence.
  Check(FarmEnclosureHelper.IsInsideEnclosure(crate,36,40),"row tile 24 inside the field");
  for(int y=11;y<=78;y++)Set(34,y,'F');UnityEngine.Time.frameCount++;
  Check(FarmEnclosureHelper.IsInsideEnclosure(crate,33,40)&&!FarmEnclosureHelper.IsInsideEnclosure(crate,35,40),"row past a fence line beyond the farm square");
  Console.WriteLine("PASS: logging row past the farm square stops at a fence");

  Clear();for(int y=0;y<100;y++)Set(8,y,'F');Set(2,50,'X');
  for(int y=41;y<=59;y++){for(int x=0;x<=7;x++)if(x!=2||y!=50)Set(x,y,'i');for(int x=9;x<=12;x++)Set(x,y,'o');}
  Expect("map edge and a long fence bound the farm");

  Clear();Set(1,1,'X');for(int x=0;x<=11;x++)for(int y=0;y<=11;y++)if(x!=1||y!=1)Set(x,y,'i');
  Expect("open land at the map corner");

  Clear();Set(50,50,'X');for(int y=48;y<=52;y++)Set(55,y,'F');
  for(int x=40;x<=60;x++)for(int y=40;y<=60;y++)if((x!=50||y!=50)&&(x!=55||y<48||y>52))Set(x,y,'i');
  Expect("ordinary farm with a short fence segment");

  // Two-tile-thick staircase diamond with a straight conveyor through a diagonal side.
  Clear();for(int x=0;x<100;x++)for(int y=0;y<100;y++){int d=Math.Abs(x-50)+Math.Abs(y-50);if(d==8||d==9)Set(x,y,'F');else if(d<8)Set(x,y,'i');else if(d<=12&&Math.Abs(x-50)<=10&&Math.Abs(y-50)<=10)Set(x,y,'o');}
  Set(54,46,'C');Set(55,46,'C');inside.Remove((50,50));Set(50,50,'X');
  Expect("staircase pen with a conveyor gap on a diagonal side");

  Clear();Rect(40,40,60,60,'F');Set(48,40,'C');Set(49,40,'C');Set(50,45,'X');
  for(int x=41;x<=59;x++)for(int y=41;y<=59;y++)if(x!=50||y!=45)Set(x,y,'i');for(int x=41;x<=59;x++)Set(x,38,'o');
  Expect("square pen with a two-tile conveyor gap");

  Clear();Rect(40,40,60,60,'F');Rect(41,41,59,59,'P');Set(48,40,'C');Set(49,40,'C');Set(48,41,'C');Set(49,41,'C');Set(50,45,'X');
  for(int x=42;x<=58;x++)for(int y=42;y<=58;y++)if(x!=50||y!=45)Set(x,y,'i');for(int x=41;x<=59;x++)Set(x,38,'o');
  Expect("fence and path wall with a gap two tiles deep");

  Clear();Rect(40,40,60,60,'F');Set(48,40,'C');Set(49,40,'C');Set(50,40,'C');Set(50,45,'X');Set(49,38,'i');
  Expect("three-tile opening is not a gap");

  // A one-wide corridor six tiles long through two-thick walls is an exit, not a gap.
  Clear();Rect(40,44,60,60,'F');Rect(41,45,59,59,'F');for(int y=38;y<=45;y++){Set(48,y,'F');Set(47,y,'F');Set(50,y,'F');Set(51,y,'F');Set(49,y,'.');}
  W.onTileMap[49,44]=-1;W.onTileMap[49,45]=-1;Set(50,47,'X');Set(49,37,'i');Set(55,37,'i');
  Expect("open corridor out of a pen");

  Clear();Rect(40,40,60,60,'F');for(int k=42;k<=58;k+=4)foreach(int v in new[]{k,k+1}){Set(v,40,'C');Set(v,60,'C');Set(40,v,'C');Set(60,v,'C');}Set(50,45,'X');for(int x=41;x<=59;x++)Set(x,38,'i');
  Expect("pen with more than 25% gaps keeps the full square");

  // Path rows with walking breaks inside a closed pen must not split it or count as fence gaps.
  foreach(bool conveyorGap in new[]{false,true}){
   Clear();Rect(40,40,60,60,'F');for(int y=43;y<=57;y+=3)for(int x=41;x<=59;x++)if(x%4!=0&&x%4!=1)Set(x,y,'P');Set(50,45,'X');
   for(int x=41;x<=59;x++){for(int y=41;y<=59;y++)if((x!=50||y!=45)&&W.tileTypeMap[x,y]!=1)Set(x,y,'i');Set(x,38,'o');}for(int y=41;y<=55;y++){Set(38,y,'o');Set(62,y,'o');}
   if(conveyorGap){Set(48,40,'C');Set(49,40,'C');}
   Expect(conveyorGap?"pen with a conveyor gap and broken path rows stays one plot":"closed pen with broken path rows inside stays one plot");
  }

  // Opening a gate only changes onTileStatusMap, so open and closed gates both bound the pen.
  foreach(int gateStatus in new[]{0,1}){
   Clear();Rect(42,42,56,56,'F');foreach(var g in new[]{(49,42),(49,56),(42,49),(56,49)}){Set(g.Item1,g.Item2,'G');W.onTileStatusMap[g.Item1,g.Item2]=gateStatus;}Set(49,47,'X');
   for(int x=43;x<=55;x++)for(int y=43;y<=55;y++)if(x!=49||y!=47)Set(x,y,'i');for(int k=43;k<=55;k++){Set(k,40,'o');Set(40,k,'o');Set(58,k,'o');}
   Expect("square pen with a "+(gateStatus==1?"open":"closed")+" gate on each side");
  }

  Clear();Rect(42,42,56,56,'F');Set(42,49,'G');W.onTileStatusMap[42,49]=1;Set(56,49,'C');Set(49,47,'X');
  for(int x=43;x<=55;x++)for(int y=43;y<=55;y++)if(x!=49||y!=47)Set(x,y,'i');for(int k=43;k<=55;k++){Set(40,k,'o');Set(58,k,'o');}
  Expect("pen with an open gate and a one-tile conveyor gap on the opposite side");

  // A 24-tree row runs north from a crate near the south fence. The far fence sits 22 tiles out.
  foreach(char farEnd in new[]{'G','C'}){
   Clear();Rect(36,28,64,52,'F');Set(50,52,farEnd);Set(36,40,'G');Set(50,30,'X');
   bool ok=true;for(int y=31;y<=51;y++)ok&=FarmEnclosureHelper.IsInsideEnclosure(crate,50,y);
   Check(ok,"row tiles inside the pen");
   Check(!FarmEnclosureHelper.IsInsideEnclosure(crate,50,53)&&!FarmEnclosureHelper.IsInsideEnclosure(crate,50,54),"row tiles past the far "+(farEnd=='G'?"gate":"conveyor gap"));
   Console.WriteLine("PASS: 24-tree row stops at a far "+(farEnd=='G'?"gate":"one-tile conveyor gap")+" 22 tiles out");
  }

  Clear();Rect(44,40,50,60,'F');Rect(50,40,56,60,'F');Set(50,50,'X');
  for(int y=41;y<=59;y++){for(int x=45;x<=55;x++)if(x!=50)Set(x,y,'i');for(int x=41;x<=43;x++){Set(x,y,'o');Set(x+16,y,'o');}}
  Expect("crate on a fence between two plots serves both");

  // A reused verdict follows edits both ways on the next frame.
  Clear();Set(49,47,'X');UnityEngine.Time.frameCount++;Check(FarmEnclosureHelper.IsInFarmArea(crate,49,40),"open land before fencing");
  Rect(42,42,56,56,'F');UnityEngine.Time.frameCount++;Check(!FarmEnclosureHelper.IsInFarmArea(crate,49,40),"new fence ignored");
  for(int x=48;x<=50;x++)W.onTileMap[x,42]=-1;UnityEngine.Time.frameCount++;Check(FarmEnclosureHelper.IsInFarmArea(crate,49,40),"three-tile opening ignored");
  Set(49,42,'G');UnityEngine.Time.frameCount++;Check(!FarmEnclosureHelper.IsInFarmArea(crate,49,40),"gate in the opening ignored");
  W.onTileMap[49,42]=-1;Set(48,42,'P');Set(49,42,'P');Set(50,42,'P');UnityEngine.Time.frameCount++;Check(!FarmEnclosureHelper.IsInFarmArea(crate,49,40),"path in the opening ignored");
  Console.WriteLine("PASS: fencing, opening, gating, and pathing a pen apply on the next frame");

  // Reused verdicts must match a fresh computation after every fence, gate, path, conveyor, or tree edit.
  Clear();var rnd=new Random(7);var crates=new List<Chest>();for(int c=0;c<6;c++)crates.Add(new Chest{xPos=30+c*8,yPos=50});Rect(25,40,75,60,'F');
  for(int frame=0;frame<1500;frame++){UnityEngine.Time.frameCount++;
   for(int e=rnd.Next(3);e>0;e--){int x=rnd.Next(20,81),y=rnd.Next(35,66);W.onTileMap[x,y]=-1;W.tileTypeMap[x,y]=0;Set(x,y,"FGPC.T"[rnd.Next(6)]);}
   foreach(var c in crates)for(int t=0;t<4;t++){int x=c.xPos+rnd.Next(-27,28),y=c.yPos+rnd.Next(-27,28);Cold.FarmEnclosureHelper.ClearState();
    Check(FarmEnclosureHelper.IsInsideEnclosure(c,x,y)==Cold.FarmEnclosureHelper.IsInsideEnclosure(c,x,y),"Reused verdict went stale at frame "+frame);}}
  Console.WriteLine("PASS: reused verdicts match fresh ones across 1500 frames of random edits");
 }
 static void Check(bool ok,string label){if(!ok)throw new Exception(label);}
}
'''

with tempfile.TemporaryDirectory(prefix="autom8er-enclosure-check-") as temp:
    directory = Path(temp)
    # C# needs every using directive ahead of the stubs.
    usings = "using System; using Autom8er;\n" + "".join(line + "\n" for line in helper.splitlines() if line.startswith("using "))
    body = "\n".join(line for line in helper.splitlines() if not line.startswith("using "))
    cold = body.replace("namespace Autom8er", "namespace Cold", 1)
    (directory / "Program.cs").write_text(usings + stubs + body + cold + program)
    project = directory / "Check.csproj"
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable></PropertyGroup></Project>')
    subprocess.run(["dotnet", "run", "--project", str(project), "-c", "Release"], cwd=directory, check=True)
