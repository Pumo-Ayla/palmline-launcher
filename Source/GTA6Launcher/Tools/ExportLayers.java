import com.jpexs.decompiler.flash.SWF;
import com.jpexs.decompiler.flash.tags.*;
import com.jpexs.decompiler.flash.tags.base.*;
import com.jpexs.decompiler.flash.types.*;
import com.jpexs.decompiler.flash.timeline.*;
import com.jpexs.decompiler.flash.exporters.commonshape.Matrix;
import java.io.*;
import java.util.*;
import java.awt.image.BufferedImage;
import javax.imageio.ImageIO;

class ExportLayers {
    final SWF swf;
    final File dir;
    final Map<Integer, Integer> ratios = new TreeMap<>();
    ExportLayers(File file, File destination) throws Exception {
        dir=destination;
        dir.mkdirs();
        try (var f=new BufferedInputStream(new FileInputStream(file))) { swf=new SWF(f,false); }
    }
    String sprite(DefineSpriteTag sprite) {
        var frames=new StringJoiner(",","[","]");
        for(Frame frame:sprite.getTimeline().getFrames()) {
            StringJoiner layers=new StringJoiner(",","[","]");
            for(DepthState state:frame.layers.values()) {
                CharacterTag c=state.getCharacter();
                if (!state.isVisible || c==null) continue;
                if (!(c instanceof DefineSpriteTag)) {
                    var old=ratios.put(c.getCharacterId(),state.ratio);
                    if(c instanceof MorphShapeTag && old!=null && old!=state.ratio) throw new IllegalStateException("animated morph");
                }
                Matrix m=state.matrix==null ? new Matrix():new Matrix(state.matrix);
                int alpha=state.colorTransForm==null ? 256:state.colorTransForm.getAlphaMulti();
                layers.add("["+state.characterId+","+state.depth+","+state.clipDepth+","+state.blendMode+","+alpha+","+state.time+","+m.scaleX+","+m.rotateSkew1+","+m.translateX/20+","+m.rotateSkew0+","+m.scaleY+","+m.translateY/20+"]");
            }
            frames.add(layers.toString());
        }
        return frames.toString();
    }
    String renderShape(int id, int ratio) throws Exception {
        var shape=(DrawableTag)swf.getCharacter(id);
        RECT rect=new RECT(shape.getRect());
        rect.Xmin=(int)Math.floor(rect.Xmin/20.0)*20-20; rect.Ymin=(int)Math.floor(rect.Ymin/20.0)*20-20;
        rect.Xmax=(int)Math.ceil(rect.Xmax/20.0)*20+20; rect.Ymax=(int)Math.ceil(rect.Ymax/20.0)*20+20;
        double zoom=Math.min(1, 81920.0/Math.max(rect.getWidth(),rect.getHeight()));
        var sprite=new DefineSpriteTag(swf);
        var place=new PlaceObject2Tag(swf);
        place.depth=1; place.characterId=id; place.placeFlagHasCharacter=true;
        if (ratio>=0) {place.placeFlagHasRatio=true;place.ratio=ratio;}
        sprite.addTag(place); sprite.addTag(new ShowFrameTag(swf));sprite.frameCount=1;
        BufferedImage art=SWF.frameToImageGet(sprite.getTimeline(),0,0,null,0,rect,new Matrix(),new ColorTransform(),null,zoom,true,1).getBufferedImage();
        ImageIO.write(art,"png",new File(dir,id+".png"));
        return "["+rect.Xmin/20.0+","+rect.Ymin/20.0+","+art.getWidth()/zoom+","+art.getHeight()/zoom+"]";
    }
    void export() throws Exception {
        var main=(DefineSpriteTag)swf.getCharacterByExportName("Animation");
        try(var o=new PrintWriter(new File(dir,"scene.json"),"UTF-8")) {
            o.print("{\"root\":"+main.spriteId+",\"fps\":"+swf.frameRate+",\"sprites\":{");
            var sprites=new StringJoiner(",");
            for(CharacterTag c:swf.getCharacters(false).values()) if(c instanceof DefineSpriteTag sp) sprites.add("\""+sp.spriteId+"\":"+sprite(sp));
            o.print(sprites); o.print("},\"shapes\":{");
            var shapes=new StringJoiner(",");
            for(var e:ratios.entrySet()) shapes.add("\""+e.getKey()+"\":"+renderShape(e.getKey(),e.getValue()));
            o.print(shapes);o.print("}}");
        }
    }
    public static void main(String[] args) throws Exception {
        System.setProperty("java.awt.headless","true");
        for(int i=2;i<args.length;i++) {
            var file=new File(args[0],args[i]+".swf"); var dir=new File(args[1],args[i]);
            new ExportLayers(file,dir).export();System.out.println(args[i]);
        }
    }
}
