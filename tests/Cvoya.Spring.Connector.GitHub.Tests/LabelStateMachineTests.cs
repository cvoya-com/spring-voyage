// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Labels;

using Shouldly;

using Xunit;

public class LabelStateMachineTests
{
    [Fact]
    public void Default_HasExpectedStatesAndInitial()
    {
        var machine = new LabelStateMachine(LabelStateMachineOptions.Default());

        machine.InitialState.ShouldBe("needs-triage");
        machine.States.ShouldContain("needs-triage");
        machine.States.ShouldContain("in-progress");
        machine.States.ShouldContain("blocked");
        machine.States.ShouldContain("resolved");
    }

    [Fact]
    public void IsStateLabel_ReturnsTrueForConfiguredLabelOnly()
    {
        var machine = new LabelStateMachine(LabelStateMachineOptions.Default());

        machine.IsStateLabel("in-progress").ShouldBeTrue();
        machine.IsStateLabel("documentation").ShouldBeFalse();
        machine.IsStateLabel("").ShouldBeFalse();
    }

    [Fact]
    public void IsLegalTransition_AllowsConfiguredMoves()
    {
        var machine = new LabelStateMachine(LabelStateMachineOptions.Default());

        machine.IsLegalTransition("needs-triage", "in-progress").ShouldBeTrue();
        machine.IsLegalTransition("in-progress", "blocked").ShouldBeTrue();
        machine.IsLegalTransition("blocked", "in-progress").ShouldBeTrue();
        machine.IsLegalTransition("blocked", "resolved").ShouldBeTrue();
    }

    [Fact]
    public void IsLegalTransition_RejectsDisallowedMoves()
    {
        var machine = new LabelStateMachine(LabelStateMachineOptions.Default());

        machine.IsLegalTransition("resolved", "in-progress").ShouldBeFalse();
        machine.IsLegalTransition("needs-triage", "blocked").ShouldBeFalse();
        machine.IsLegalTransition("in-progress", "needs-triage").ShouldBeFalse();
    }

    [Fact]
    public void IsLegalTransition_FromNull_OnlyAllowsInitialState()
    {
        var machine = new LabelStateMachine(LabelStateMachineOptions.Default());

        machine.IsLegalTransition(null, "needs-triage").ShouldBeTrue();
        machine.IsLegalTransition(null, "in-progress").ShouldBeFalse();
    }

    [Fact]
    public void IsLegalTransition_UnknownTargetIsAlwaysIllegal()
    {
        var machine = new LabelStateMachine(LabelStateMachineOptions.Default());

        machine.IsLegalTransition("needs-triage", "never-heard-of-it").ShouldBeFalse();
    }

    [Fact]
    public void Derive_Labeled_FromPreviousStateLabel()
    {
        var machine = new LabelStateMachine(LabelStateMachineOptions.Default());

        // Event adds "in-progress" to an issue that still carries "needs-triage"
        // in the post-change label set.
        var transition = machine.Derive(
            currentLabels: ["needs-triage", "in-progress", "bug"],
            changedLabel: "in-progress",
            action: "labeled");

        transition.ShouldNotBeNull();
        transition!.From.ShouldBe("needs-triage");
        transition.To.ShouldBe("in-progress");
        transition.Trigger.ShouldBe("labeled");
        transition.Legal.ShouldBeTrue();
    }

    [Fact]
    public void Derive_Labeled_UnrelatedLabel_ReturnsNull()
    {
        var machine = new LabelStateMachine(LabelStateMachineOptions.Default());

        machine.Derive(
            currentLabels: ["bug"],
            changedLabel: "bug",
            action: "labeled").ShouldBeNull();
    }

    [Fact]
    public void Derive_Unlabeled_StateLabel_DerivesFromRemoved()
    {
        var machine = new LabelStateMachine(LabelStateMachineOptions.Default());

        // "in-progress" removed; nothing else in the state set remains.
        var transition = machine.Derive(
            currentLabels: ["bug"],
            changedLabel: "in-progress",
            action: "unlabeled");

        transition.ShouldNotBeNull();
        transition!.From.ShouldBe("in-progress");
        transition.To.ShouldBeNull();
        transition.Trigger.ShouldBe("unlabeled");
        transition.Legal.ShouldBeTrue();
    }

    [Fact]
    public void Derive_IllegalMove_MarkedLegalFalse()
    {
        var machine = new LabelStateMachine(LabelStateMachineOptions.Default());

        // Issue currently has "resolved"; someone adds "needs-triage" — not allowed.
        var transition = machine.Derive(
            currentLabels: ["resolved", "needs-triage"],
            changedLabel: "needs-triage",
            action: "labeled");

        transition.ShouldNotBeNull();
        transition!.From.ShouldBe("resolved");
        transition.To.ShouldBe("needs-triage");
        transition.Legal.ShouldBeFalse();
    }

    [Fact]
    public void ValidTransitionsFrom_ReturnsConfiguredTargets()
    {
        var machine = new LabelStateMachine(LabelStateMachineOptions.Default());

        var valid = machine.ValidTransitionsFrom("in-progress");
        valid.ShouldContain("blocked");
        valid.ShouldContain("resolved");
    }

    [Fact]
    public void ValidTransitionsFrom_UnknownState_ReturnsEmpty()
    {
        var machine = new LabelStateMachine(LabelStateMachineOptions.Default());
        machine.ValidTransitionsFrom("unknown").Count.ShouldBe(0);
    }

    [Fact]
    public void OverriddenConfig_UsesCustomVocabulary()
    {
        var machine = new LabelStateMachine(new LabelStateMachineOptions
        {
            States = ["queued", "working", "done"],
            Transitions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["queued"] = ["working"],
                ["working"] = ["done"],
            },
            InitialState = "queued",
        });

        machine.IsStateLabel("needs-triage").ShouldBeFalse();
        machine.IsStateLabel("queued").ShouldBeTrue();
        machine.IsLegalTransition("queued", "working").ShouldBeTrue();
        machine.IsLegalTransition("working", "queued").ShouldBeFalse();
    }
}