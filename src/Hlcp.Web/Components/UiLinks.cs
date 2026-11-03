using Hlcp.Application;

namespace Hlcp.Web.Components;

public static class UiLinks
{
    public static string Node(string node) => "/nodes/" + Uri.EscapeDataString(node);
    public static string Resource(string node, string resource) => Node(node) + "/resources/" + Uri.EscapeDataString(resource);
    public static string Logs(string node, string resource) => Resource(node, resource) + "/logs";
    public static string NodeState(string state) => state switch { "online" => "Online", "offline" => "Offline", "degraded" => "Omezené spojení", _ => "Neznámý stav" };
    public static string ResourceState(ResourceDisplayState state) => state switch { ResourceDisplayState.Running => "Běží", ResourceDisplayState.Stopped => "Zastaveno", ResourceDisplayState.Error => "Chyba", ResourceDisplayState.Stale => "Zastaralé", _ => "Neznámý" };
    public static string StateColor(ResourceDisplayState state) => state switch { ResourceDisplayState.Running => "bg-emerald-50 text-emerald-800", ResourceDisplayState.Error => "bg-red-50 text-red-800", ResourceDisplayState.Stale => "bg-amber-50 text-amber-900", _ => "bg-slate-100 text-slate-600" };
    public static string Time(DateTimeOffset value) => value == default ? "Neznámý" : value.ToLocalTime().ToString("dd. MM. yyyy HH:mm:ss zzz");
}
