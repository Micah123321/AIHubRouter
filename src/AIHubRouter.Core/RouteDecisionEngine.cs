namespace AIHubRouter.Core;

public sealed record RouteDecisionResult(RouteDecision Decision, RouteState NextState);

public static class RouteDecisionEngine
{
    public static RouteDecisionResult Decide(
        RouteEvaluation evaluation,
        RouteState state,
        BalancedRoutingPolicy policy,
        DateTimeOffset now,
        long? observedCurrentGroupId = null)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        var currentGroupId = observedCurrentGroupId ?? state.CurrentGroupId;
        var current = evaluation.EligibleCandidates.FirstOrDefault(candidate =>
            candidate.Group.Id == currentGroupId);
        var target = evaluation.Recommended;

        if (target is null)
        {
            return Result(
                current,
                null,
                false,
                RouteDecisionReason.NoCandidate,
                state with { CurrentGroupId = currentGroupId },
                0,
                null,
                now);
        }

        var premium = CalculatePremium(evaluation.MinimumMultiplier, target.EffectiveMultiplier);
        if (currentGroupId is null)
        {
            return Switched(current, target, RouteDecisionReason.InitialRoute, state, premium, null, now);
        }

        if (current is null)
        {
            return Switched(current, target, RouteDecisionReason.CurrentRouteInvalid, state, premium, null, now);
        }

        if (current.Group.Id == target.Group.Id)
        {
            return Result(
                current,
                target,
                false,
                RouteDecisionReason.AlreadyOptimal,
                state with { CurrentGroupId = current.Group.Id },
                premium,
                0,
                now);
        }

        var latencyImprovement = CalculateLatencyImprovement(
            current.Provider.FirstTokenLatencyMs,
            target.Provider.FirstTokenLatencyMs);
        var currentScore = RoutingEngine.CalculateWeightedScore(evaluation, current);
        var targetScore = RoutingEngine.CalculateWeightedScore(evaluation, target);
        if (currentScore is { } currentScoreValue &&
            targetScore is { } targetScoreValue &&
            targetScoreValue - currentScoreValue <= policy.MinimumScoreAdvantageToSwitch)
        {
            return Result(
                current,
                current,
                false,
                RouteDecisionReason.ScoreAdvantageTooSmall,
                state with { CurrentGroupId = current.Group.Id },
                CalculatePremium(evaluation.MinimumMultiplier, current.EffectiveMultiplier),
                0,
                now);
        }

        return Switched(
            current,
            target,
            target.EffectiveMultiplier < current.EffectiveMultiplier
                ? RouteDecisionReason.BetterPrice
                : RouteDecisionReason.FasterForWeightedTradeoff,
            state,
            premium,
            latencyImprovement,
            now);
    }

    private static RouteDecisionResult Switched(
        RouteCandidate? current,
        RouteCandidate target,
        RouteDecisionReason reason,
        RouteState state,
        double premium,
        double? latencyImprovement,
        DateTimeOffset now)
    {
        var next = state with
        {
            CurrentGroupId = target.Group.Id
        };
        return Result(current, target, true, reason, next, premium, latencyImprovement, now);
    }

    private static RouteDecisionResult Result(
        RouteCandidate? current,
        RouteCandidate? target,
        bool shouldSwitch,
        RouteDecisionReason reason,
        RouteState state,
        double premium,
        double? latencyImprovement,
        DateTimeOffset now)
    {
        return new RouteDecisionResult(
            new RouteDecision(
                current,
                target,
                shouldSwitch,
                reason,
                premium,
                latencyImprovement,
                now),
            state);
    }

    private static double CalculatePremium(double? minimum, double value)
    {
        if (minimum is null || minimum <= 0)
        {
            return value <= 0 ? 0 : double.PositiveInfinity;
        }

        return Math.Max(0, (value - minimum.Value) / minimum.Value * 100);
    }

    private static double? CalculateLatencyImprovement(double? current, double? target)
    {
        if (current is not { } currentValue ||
            target is not { } targetValue ||
            !double.IsFinite(currentValue) ||
            !double.IsFinite(targetValue) ||
            currentValue <= 0 ||
            targetValue < 0)
        {
            return null;
        }

        return (currentValue - targetValue) / currentValue * 100;
    }
}
