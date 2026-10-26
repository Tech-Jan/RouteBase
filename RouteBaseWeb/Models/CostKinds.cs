namespace RouteBaseWeb.Models;

/// <summary>
/// How <c>CumulativeCostKj</c> was obtained (D2): a power meter or the physics/metabolic model.
/// On the wire (EffortPerformanceData.costKind, stamped) — drives dashed-vs-solid effort-cost
/// lines. The sibling <c>bool CostEstimated</c> is a deliberate wire duplicate: three TS
/// consumers read the bool today, so removing it is a wire change deferred past this pass.
/// </summary>
public static class CostKinds
{
    public const string Measured  = "measured";
    public const string Estimated = "estimated";
}
