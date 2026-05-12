using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(15)]
public class M0015_AddLlmReviewerSettings : Migration
{
    public override void Up()
    {
        Insert.IntoTable("settings").Row(new { key = "llm_reviewer_enabled", value = "false" });
        Insert.IntoTable("settings").Row(new { key = "llm_reviewer_provider", value = "openai" });
        Insert.IntoTable("settings").Row(new { key = "llm_reviewer_sample_percent", value = "10" });
        Insert.IntoTable("settings").Row(new { key = "llm_reviewer_log_attempts", value = "true" });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = "llm_reviewer_enabled" });
        Delete.FromTable("settings").Row(new { key = "llm_reviewer_provider" });
        Delete.FromTable("settings").Row(new { key = "llm_reviewer_sample_percent" });
        Delete.FromTable("settings").Row(new { key = "llm_reviewer_log_attempts" });
    }
}
