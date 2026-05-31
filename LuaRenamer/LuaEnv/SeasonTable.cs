// ReSharper disable InconsistentNaming

using LuaRenamer.LuaEnv.Attributes;
using LuaRenamer.LuaEnv.BaseTypes;

namespace LuaRenamer.LuaEnv;

[LuaType(LuaTypeNames.Season)]
public class SeasonTable : Table
{
    [LuaType(LuaTypeNames.integer, "Season year")]
    public string year => Get();

    [LuaType(nameof(EnumsTable.SeasonName), "Season aired")]
    public string season => Get();
}
