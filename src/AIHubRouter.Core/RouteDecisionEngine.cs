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
                state with { CurrentGroupId = currentGroupId, PendingGroupId = null, PendingConfirmationCount = 0 },
                0,
                null,
                0,
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
                state with
                {
                    CurrentGroupId = current.Group.Id,
                    PendingGroupId = null,
                    PendingConfirmationCount = 0
                },
                premium,
                0,
                0,
                now);
        }

        var priceImprovement = CalculateImprovement(
            current.EffectiveMultiplier,
            target.EffectiveMultiplier);
        var latencyImprovement = CalculateLatencyImprovement(
            current.Provider.FirstTokenLatencyMs,
            target.Provider.FirstTokenLatencyMs);

        if (priceImprovement is not null && priceImprovement >= policy.MinimumPriceImprovementPercent)
        {
            return Switched(current, target, RouteDecisionReason.BetterPrice, state, premium, latencyImprovement, now);
        }

        if (latencyImprovement is null || latencyImprovement < policy.MinimumLatencyImprovementPercent)
        {
            return Result(
                current,
                target,
                false,
                RouteDecisionReason.InsufficientImprovement,
                state with
                {
                    CurrentGroupId = current.Group.Id,
                    PendingGroupId = null,
                    PendingConfirmationCount = 0
                },
                premium,
                latencyImprovement,
                0,
                now);
        }

        var confirmationCount = state.PendingGroupId == target.Group.Id
            ? state.PendingConfirmationCount + 1
            : 1;
        var pendingState = state with
        {
            CurrentGroupId = current.Group.Id,
            PendingGroupId = target.Group.Id,
            PendingConfirmationCount = confirmationCount
        };

        if (confirmationCount < policy.RequiredConfirmations)
        {
            return Result(
                current,
                target,
                false,
                RouteDecisionReason.AwaitingConfirmation,
                pendingState,
                premium,
                latencyImprovement,
                confirmationCount,
                now);
        }

        if (state.LastSwitchAt is { } lastSwitchAt && now - lastSwitchAt < policy.MinimumDwellTime)
        {
            return Result(
                current,
                target,
                false,
                RouteDecisionReason.MinimumDwellTime,
                pendingState,
                premium,
                latencyImprovement,
                confirmationCount,
                now);
        }

        return Switched(
            current,
            target,
            RouteDecisionReason.FasterWithinBudget,
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
            CurrentGroupId = target.Group.Id,
            PendingGroupId = null,
            PendingConfirmationCount = 0,
            LastSwitchAt = now
        };
        return Result(current, target, true, reason, next, premium, latencyImprovement, 0, now);
    }

    private static RouteDecisionResult Result(
        RouteCandidate? current,
        RouteCandidate? target,
        bool shouldSwitch,
        RouteDecisionReason reason,
        RouteState state,
        double premium,
        double? latencyImprovement,
        int confirmationCount,
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
                confirmationCount,
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

    private static double? CalculateImprovement(double current, double target)
    {
        if (!double.IsFinite(current) || !double.IsFinite(target) || current <= 0 || target < 0)
        {
            return null;
        }

        return (current - target) / current * 100;
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

        return CalculateImprovement(currentValue, targetValue);
    }
}
