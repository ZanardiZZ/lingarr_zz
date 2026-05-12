using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(12)]
public class M0012_AddSelectiveRetryReporting : Migration
{
    public override void Up()
    {
        Alter.Table("translation_requests")
            .AddColumn("selective_retry_attempted_count").AsInt32().Nullable()
            .AddColumn("selective_retry_improved_count").AsInt32().Nullable()
            .AddColumn("selective_retry_failed_count").AsInt32().Nullable()
            .AddColumn("selective_retry_skipped_count").AsInt32().Nullable()
            .AddColumn("selective_retry_reason_counts_json").AsCustom("TEXT").Nullable();
    }

    public override void Down()
    {
        Delete.Column("selective_retry_attempted_count").FromTable("translation_requests");
        Delete.Column("selective_retry_improved_count").FromTable("translation_requests");
        Delete.Column("selective_retry_failed_count").FromTable("translation_requests");
        Delete.Column("selective_retry_skipped_count").FromTable("translation_requests");
        Delete.Column("selective_retry_reason_counts_json").FromTable("translation_requests");
    }
}
