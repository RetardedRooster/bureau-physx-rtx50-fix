import java.nio.*; import java.nio.file.*; import java.util.*;
// The Bureau: maps each original (sm_10..sm_30 only) CUDA fatbin to NVIDIA's updated PTX fatbin.
// Pair <oldDll> <newDll> <outDir>: matches fatbins by kernel-name set, compares sm_30 cubin interfaces,
// writes outDir/<fnv(oldFatbin)>.ptx = new fatbin when the interfaces are identical.
public class PairFatbins {
 static List<byte[]> fatbins(byte[] d){ ByteBuffer b=ByteBuffer.wrap(d).order(ByteOrder.LITTLE_ENDIAN); List<byte[]> r=new ArrayList<>();
  for(int i=0;i+16<d.length;i++) if(b.getInt(i)==0xBA55ED50){ int hs=b.getShort(i+6)&0xffff; long fs=b.getLong(i+8); r.add(Arrays.copyOfRange(d,i,(int)(i+hs+fs))); } return r; }
 static byte[] cubin(byte[] f, int arch){ ByteBuffer b=ByteBuffer.wrap(f).order(ByteOrder.LITTLE_ENDIAN); int hs=b.getShort(6)&0xffff; long fs=b.getLong(8); int p=hs; long end=hs+fs;
  while(p+64<=end){ int kind=b.getShort(p)&0xffff; int ehs=b.getInt(p+4); long sz=b.getLong(p+8); int ar=b.getInt(p+28); if(kind==2&&ar==arch) return Arrays.copyOfRange(f,p+ehs,(int)(p+ehs+sz)); p+=ehs+sz; } return null; }
 // interface signature: kernels with nv.info param entries (EIATTR_KPARAM_INFO 0x17 records), plus symbol names of objects
 static TreeMap<String,String> iface(byte[] e){ TreeMap<String,String> m=new TreeMap<>(); ByteBuffer b=ByteBuffer.wrap(e).order(ByteOrder.LITTLE_ENDIAN);
  int shoff=b.getInt(0x20), se=b.getShort(0x2e)&0xffff, sn=b.getShort(0x30)&0xffff, ss=b.getShort(0x32)&0xffff; int stro=b.getInt(shoff+ss*se+0x10);
  int symOff=-1,symSz=0,strOff=-1;
  for(int i=0;i<sn;i++){ int s=shoff+i*se; int o=stro+b.getInt(s); int z=o; while(e[z]!=0)z++; String n=new String(e,o,z-o); int off=b.getInt(s+0x10), sz=b.getInt(s+0x14); int type=b.getInt(s+4);
   if(type==2){symOff=off;symSz=sz; strOff=b.getInt(shoff+b.getInt(s+0x18)*se+0x10);}
   if(n.startsWith(".nv.info.")){ StringBuilder h=new StringBuilder(); int q=off; while(q+4<=off+sz){ int fmt=e[q]&0xff, attr=e[q+1]&0xff; int len= fmt==4? (b.getShort(q+2)&0xffff) : (fmt==3?2:(fmt==2?0:0)); if(fmt==4 && (attr==0x17||attr==0x19||attr==0x0a)){ if(attr==0x0a){ h.append("cbank:").append(b.getShort(q+4+4)&0xffff).append(","); } else { for(int k=0;k<len;k++) h.append(String.format("%02x",e[q+4+k])); h.append(attr==0x19?"|size ":";"); } } q+=4+(fmt==4?len:(fmt==3?0:0)); if(fmt!=4&&fmt!=3) q+=0; }
    m.put("K "+n.substring(9), h.toString()); }
   if(n.startsWith(".nv.constant0.")) m.put("C0 "+n.substring(14), ""+sz);
  }
  if(symOff>=0) for(int q=symOff;q+16<=symOff+symSz;q+=16){ int no=b.getInt(q); int info=e[q+12]&0xff; int sz=b.getInt(q+8); int o=strOff+no; int z=o; while(e[z]!=0)z++; String n=new String(e,o,z-o); int t=info&0xf; if(n.isEmpty()||n.startsWith(".")) continue; if(t==1||t==0) m.put("S "+n, "type"+t+" size"+sz); }
  return m; }
 static long fnv(byte[] p){ long h=2166136261L; for(byte x:p){ h^=(x&0xff); h=(h*16777619L)&0xffffffffL; } return h; }
 public static void main(String[] a) throws Exception { List<byte[]> o=fatbins(Files.readAllBytes(Path.of(a[0]))), n=fatbins(Files.readAllBytes(Path.of(a[1]))); Path out=Path.of(a[2]); Files.createDirectories(out);
  System.out.println(Path.of(a[0]).getFileName()+": old "+o.size()+" fatbins, new "+n.size());
  for(byte[] of:o){ TreeMap<String,String> oi=iface(cubin(of,30)); Set<String> ok=new TreeSet<>(); for(String k:oi.keySet()) if(k.startsWith("K ")) ok.add(k);
   byte[] match=null; TreeMap<String,String> ni=null;
   for(byte[] nf:n){ byte[] c=cubin(nf,30); if(c==null) continue; TreeMap<String,String> t=iface(c); Set<String> nk=new TreeSet<>(); for(String k:t.keySet()) if(k.startsWith("K ")) nk.add(k); if(nk.equals(ok)){ match=nf; ni=t; break; } }
   String h=String.format("%08x",fnv(of)); String kn=ok.toString(); if(kn.length()>90) kn=kn.substring(0,90)+"..";
   if(match==null){ System.out.println("  "+h+" NO MATCH "+kn); continue; }
   List<String> diffs=new ArrayList<>(); TreeSet<String> all=new TreeSet<>(oi.keySet()); all.addAll(ni.keySet());
   for(String k:all){ String x=oi.get(k), y=ni.get(k); if(!Objects.equals(x,y)) diffs.add(k+": "+x+" -> "+y); }
   if(diffs.isEmpty()){ Files.write(out.resolve(h+".ptx"), match); System.out.println("  "+h+" OK  -> new fatbin "+match.length+" B  "+kn); }
   else { System.out.println("  "+h+" DIFF "+kn); for(String s:diffs.subList(0,Math.min(8,diffs.size()))) System.out.println("      "+s); }
  } } }
