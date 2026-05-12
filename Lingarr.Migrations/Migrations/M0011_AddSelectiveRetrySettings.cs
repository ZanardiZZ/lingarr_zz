using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(11)]
public class M0011_AddSelectiveRetrySettings : Migration
{
    public override void Up()
    {
        Insert.IntoTable("settings").Row(new { key = "selective_retry_enabled", value = "true" });
        Insert.IntoTable("settings").Row(new { key = "selective_retry_max_attempts", value = "1" });
        Insert.IntoTable("settings").Row(new { key = "selective_retry_high_severity_only", value = "true" });
        Insert.IntoTable("settings").Row(new { key = "selective_retry_provider_scope", value = "llm_only" });
        Insert.IntoTable("settings").Row(new { key = "selective_retry_log_attempts", value = "true" });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = "selective_retry_enabled" });
        Delete.FromTable("settings").Row(new { key = "selective_retry_max_attempts" });
        Delete.FromTable("settings").Row(new { key = "selective_retry_high_severity_only" });
        Delete.FromTable("settings").Row(new { key = "selective_retry_provider_scope" });
        Delete.FromTable("settings").Row(new { key = "selective_retry_log_attempts" });
    }
}
