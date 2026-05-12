using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(13)]
public class M0013_AddSelectiveRetryDecisionSettings : Migration
{
    public override void Up()
    {
        Insert.IntoTable("settings").Row(new { key = "selective_retry_score_threshold", value = "25" });
        Insert.IntoTable("settings").Row(new { key = "selective_retry_improvement_margin", value = "10" });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = "selective_retry_score_threshold" });
        Delete.FromTable("settings").Row(new { key = "selective_retry_improvement_margin" });
    }
}
