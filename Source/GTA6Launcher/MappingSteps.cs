using System;

internal sealed class MappingStep
{
    public readonly string Id, Joint, Label, Symbol;
    public readonly PadState Demo;
    public bool Shoulder { get { return Joint=="LB" || Joint=="RB" || Joint=="LT" || Joint=="RT"; } }
    MappingStep(string id,string joint,string label,string symbol,PadState demo) {
        Id=id; Joint=joint; Label=label; Symbol=symbol; Demo=demo;
    }
    static MappingStep Button(string id,string label,string symbol,string joint=null) {
        int bit=Array.IndexOf(PadState.ButtonNames,id);
        return new MappingStep(id,joint ?? id,label,symbol,new PadState { Buttons=1u<<bit });
    }
    public static readonly MappingStep[] All={
        Button("A","A 键","A"), Button("B","B 键","B"), Button("X","X 键","X"), Button("Y","Y 键","Y"),
        Button("LB","左肩键","LB"), Button("RB","右肩键","RB"),
        new MappingStep("LT","LT","左扳机","LT",new PadState { LT=1 }),
        new MappingStep("RT","RT","右扳机","RT",new PadState { RT=1 }),
        Button("Up","方向键上","↑","Dpad"), Button("Down","方向键下","↓","Dpad"),
        Button("Left","方向键左","←","Dpad"), Button("Right","方向键右","→","Dpad"),
        Button("LS","按下左摇杆","LS"), Button("RS","按下右摇杆","RS"),
        Button("View","视图键","▢"), Button("Menu","菜单键","☰"), Button("Share","分享键","⇧"), Button("Guide","Xbox 键","Xbox")
    };
}
