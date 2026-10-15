using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Taverna.Core.Agents;

namespace Taverna.App.ViewModels;

public partial class PermissionChoiceViewModel(PermissionOption option) : ObservableObject
{
    public AgentPermissionMode Mode => option.Mode;
    public string Title => Mode.ToString();
    public string Description => option.Description;
    public bool IsSupported => option.IsSupported;
    public Geometry Icon => Geometry.Parse(Mode switch
    {
        AgentPermissionMode.Manual => "M5,11 L5,7 Q5,5 7,5 L7,3 Q8,1 9,3 L9,7 L9,2 Q11,0 11,2 L11,7 L11,3 Q13,1 13,3 L13,8 Q15,6 15,9 L15,12 Q15,16 10,16 Q7,16 5,13 L2,10 Q2,8 4,9 Z",
        AgentPermissionMode.Auto => "M10,1 L4,9 L9,9 L7,16 L14,7 L9,7 Z",
        _ => "M3,2 L11,2 L14,5 L14,16 L3,16 Z M6,7 L11,7 M6,10 L11,10 M6,13 L10,13"
    });
    [ObservableProperty] private bool isSelected;
}
