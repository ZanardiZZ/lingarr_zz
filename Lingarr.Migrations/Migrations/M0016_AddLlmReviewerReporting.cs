using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(16)]
public class M0016_AddLlmReviewerReporting : Migration
{
    public override void Up()
    {
        Alter.Table("translation_requests")
            .AddColumn("llm_review_reviewed_count").AsInt32().Nullable()
            .AddColumn("llm_review_changed_count").AsInt32().Nullable()
            .AddColumn("llm_review_failed_count").AsInt32().Nullable()
            .AddColumn("llm_review_suspicious_reviewed_count").AsInt32().Nullable()
            .AddColumn("llm_review_sampled_reviewed_count").AsInt32().Nullable()
            .AddColumn("llm_review_provider").AsString().Nullable()
            .AddColumn("llm_review_reason_counts_json").AsCustom("TEXT").Nullable();
    }

    public override void Down()
    {
        Delete.Column("llm_review_reviewed_count").FromTable("translation_requests");
        Delete.Column("llm_review_changed_count").FromTable("translation_requests");
        Delete.Column("llm_review_failed_count").FromTable("translation_requests");
        Delete.Column("llm_review_suspicious_reviewed_count").FromTable("translation_requests");
        Delete.Column("llm_review_sampled_reviewed_count").FromTable("translation_requests");
        Delete.Column("llm_review_provider").FromTable("translation_requests");
        Delete.Column("llm_review_reason_counts_json").FromTable("translation_requests");
    }
}
