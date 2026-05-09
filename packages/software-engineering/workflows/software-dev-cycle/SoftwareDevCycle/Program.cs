// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Packages.SoftwareEngineering.Workflows;

using Cvoya.Spring.Packages.SoftwareEngineering.Workflows.Activities;
using global::Dapr.Workflow;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddDaprWorkflow(options =>
        {
            options.RegisterWorkflow<SoftwareDevCycleWorkflow>();
            options.RegisterActivity<TriageActivity>();
            options.RegisterActivity<AssignByExpertiseActivity>();
            options.RegisterActivity<CreatePlanActivity>();
            options.RegisterActivity<ImplementActivity>();
            options.RegisterActivity<ReviewActivity>();
            options.RegisterActivity<MergeActivity>();
        });

        var app = builder.Build();

        app.Run();
    }
}
