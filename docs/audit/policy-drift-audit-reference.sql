DECLARE @ReportTitle VARCHAR(100) = '108042D0 | 2026-05-04';

-- Parse ExecutionID and Date from report title
DECLARE @ExecutionID VARCHAR(64) =
    LTRIM(RTRIM(SUBSTRING(@ReportTitle, 1, CHARINDEX('|', @ReportTitle) - 1)));

DECLARE @ReportDate DATE =
    CAST(LTRIM(RTRIM(SUBSTRING(@ReportTitle, CHARINDEX('|', @ReportTitle) + 1, LEN(@ReportTitle)))) AS DATE);

-- Derive log date range (previous day through end of report date)
DECLARE @LogStartDate DATETIME2 = CAST(DATEADD(DAY, -1, @ReportDate) AS DATETIME2);
DECLARE @LogEndDate   DATETIME2 = CAST(DATEADD(DAY,  1, @ReportDate) AS DATETIME2);

-- Summary Statistics
DECLARE @TotalPoliciesInCA INT;
DECLARE @TotalPoliciesInReport INT;    -- CA n PDE
DECLARE @TotalExcluded INT;            -- CA only
DECLARE @TotalPDEOnly INT;             -- PDE only (diagnostic only)

;WITH CASet AS (
    SELECT DISTINCT CA.PolicyID
    FROM [dbo].CA_EPV_REPORTING CA
    WHERE CA.PolicyID IS NOT NULL
      AND CA.PolicyID<>'null'
),
PDESet AS (
    SELECT DISTINCT PDE.PolicyID
    FROM [EPV_REPORTING].[unity].[PolicyDriftEval] PDE
    WHERE PDE.ExecutionId LIKE @ExecutionID+'%'
      AND PDE.PolicyID IS NOT NULL
)
SELECT
    @TotalPoliciesInCA=(SELECT COUNT(*) FROM CASet),
    @TotalPoliciesInReport=(SELECT COUNT(*) FROM CASet C INNER JOIN PDESet P ON P.PolicyID=C.PolicyID),
    @TotalExcluded=(SELECT COUNT(*) FROM CASet C WHERE NOT EXISTS (SELECT 1 FROM PDESet P WHERE P.PolicyID=C.PolicyID)),
    @TotalPDEOnly=(SELECT COUNT(*) FROM PDESet P WHERE NOT EXISTS (SELECT 1 FROM CASet C WHERE C.PolicyID=P.PolicyID));

SELECT
    @ReportTitle AS ReportTitle,
    @ExecutionID AS ExecutionID,
    @ReportDate AS ReportDate,
    @TotalPoliciesInCA AS [Policies In CA_EPV_REPORTING],
    @TotalExcluded AS [Total Excluded],
    @TotalPoliciesInReport AS [Total Processed],
    (SELECT COUNT(*)
     FROM [EPV_REPORTING].[unity].[PolicyDriftEval]
     WHERE ExecutionId LIKE @ExecutionID+'%'
       AND Status='NO_DRIFT') AS [No Drift],
    (SELECT COUNT(*)
     FROM [EPV_REPORTING].[unity].[PolicyDriftEval]
     WHERE ExecutionId LIKE @ExecutionID+'%'
       AND Status='DRIFT') AS [Drift Detected],
    (SELECT COUNT(*)
     FROM [EPV_REPORTING].[unity].[PolicyDriftEval]
     WHERE ExecutionId LIKE @ExecutionID+'%'
       AND Status='MISSING_BASELINE') AS [Missing Baseline];

;WITH ExcludedPolicies AS (
    SELECT DISTINCT CA.PolicyID
    FROM [dbo].CA_EPV_REPORTING CA
    LEFT JOIN [EPV_REPORTING].[unity].[PolicyDriftEval] PDE
        ON PDE.PolicyID=CA.PolicyID
       AND PDE.ExecutionId LIKE @ExecutionID+'%'
    WHERE CA.PolicyID IS NOT NULL
      AND CA.PolicyID<>'null'
      AND PDE.PolicyID IS NULL
),
PolicyState AS (
    SELECT CA.PolicyID,
        CASE
            WHEN MIN(CASE WHEN CA.CAFDeletionDate IS NULL THEN 0 ELSE 1 END)=0
             AND MAX(CASE WHEN CA.CAFDeletionDate IS NULL THEN 0 ELSE 1 END)=1
                THEN 'YES+'
            WHEN MAX(CASE WHEN CA.CAFDeletionDate IS NULL THEN 0 ELSE 1 END)=0
                THEN 'NO'
            ELSE 'YES'
        END AS IsDeletionCandidate
    FROM [dbo].CA_EPV_REPORTING CA
    WHERE CA.PolicyID IS NOT NULL
      AND CA.PolicyID<>'null'
    GROUP BY CA.PolicyID
),
Tally AS (
    SELECT TOP (400)
        ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N
    FROM sys.all_objects
),
CSharpFilterSimulation AS (
    SELECT
        EP.PolicyID,
        STRING_AGG(SUBSTRING(EP.PolicyID,T.N,1),'')
            WITHIN GROUP (ORDER BY T.N) AS FilteredPolicyID
    FROM ExcludedPolicies EP
    INNER JOIN Tally T
        ON T.N<=LEN(EP.PolicyID)
    WHERE SUBSTRING(EP.PolicyID,T.N,1)
          COLLATE Latin1_General_BIN2 LIKE '[A-Za-z0-9-]'
    GROUP BY EP.PolicyID
),
CSharpFilterFindings AS (
    SELECT
        EP.PolicyID,
        COALESCE(CFS.FilteredPolicyID,'') AS CSharpFilteredPolicyID,
        CASE
            WHEN COALESCE(CFS.FilteredPolicyID,'')='' THEN 1
            WHEN CFS.FilteredPolicyID<>EP.PolicyID THEN 1
            ELSE 0
        END AS IsCSharpFilterMutation,
        CASE
            WHEN COALESCE(CFS.FilteredPolicyID,'')=''
                THEN 'CSHARP_POLICY_FILTER_REMOVED_ALL_CHARACTERS'
            WHEN CFS.FilteredPolicyID<>EP.PolicyID
                THEN 'CSHARP_POLICY_FILTER_MUTATED_POLICYID'
            ELSE NULL
        END AS CSharpFilterReason
    FROM ExcludedPolicies EP
    LEFT JOIN CSharpFilterSimulation CFS
        ON CFS.PolicyID=EP.PolicyID
),
FilteredLogs AS (
    SELECT
        Id,
        [Message],
        [TimeStamp],
        ROW_NUMBER() OVER (ORDER BY Id) AS RowNum
    FROM [EPV_REPORTING].[unity].[LogEvents]
    WHERE [TimeStamp]>=@LogStartDate
      AND [TimeStamp]<@LogEndDate
),
PolicyBlockLogs AS (
    SELECT
        FL.*,
        SUM(
            CASE
                WHEN FL.[Message] LIKE 'Exporting policy ZIP for platform "%'
                    THEN 1
                ELSE 0
            END
        ) OVER (ORDER BY FL.RowNum) AS PolicyBlockID
    FROM FilteredLogs FL
),
ParsedLogs AS (
    SELECT
        PBL.Id,
        PBL.[TimeStamp],
        PBL.RowNum,
        PBL.PolicyBlockID,
        PBL.[Message],
        COALESCE(QP.QuotedPolicyID,UP.UrlPolicyID) AS ParsedPolicyID,
        CASE
            WHEN PBL.[Message] LIKE '% - 400%'
                THEN 1
            ELSE 0
        END AS HasHttp400,
        CASE
            WHEN PBL.[Message] LIKE '%400 Bad Request%'
                THEN 1
            ELSE 0
        END AS HasBadRequest,
        CASE
            WHEN PBL.[Message] LIKE '%No ZIP content returned for platform "%'
                THEN 1
            ELSE 0
        END AS HasNoZip
    FROM PolicyBlockLogs PBL

    OUTER APPLY (
        SELECT CHARINDEX('platform "',PBL.[Message]) AS PlatformStart
    ) Q1

    OUTER APPLY (
        SELECT
            CASE
                WHEN Q1.PlatformStart>0
                    THEN Q1.PlatformStart+LEN('platform "')
            END AS QuotedValueStart
    ) Q2

    OUTER APPLY (
        SELECT
            CASE
                WHEN Q2.QuotedValueStart IS NOT NULL
                    THEN CHARINDEX('"',PBL.[Message],Q2.QuotedValueStart)
            END AS QuotedValueEnd
    ) Q3

    OUTER APPLY (
        SELECT
            CASE
                WHEN Q2.QuotedValueStart IS NOT NULL
                 AND Q3.QuotedValueEnd>Q2.QuotedValueStart
                    THEN SUBSTRING(
                        PBL.[Message],
                        Q2.QuotedValueStart,
                        Q3.QuotedValueEnd-Q2.QuotedValueStart
                    )
            END AS QuotedPolicyID
    ) QP

    OUTER APPLY (
        SELECT
            CHARINDEX('/Platforms/',PBL.[Message]) AS UrlPlatformStart,
            CHARINDEX('/Export',PBL.[Message]) AS UrlExportStart
    ) U1

    OUTER APPLY (
        SELECT
            CASE
                WHEN U1.UrlPlatformStart>0
                 AND U1.UrlExportStart>U1.UrlPlatformStart
                    THEN SUBSTRING(
                        PBL.[Message],
                        U1.UrlPlatformStart+LEN('/Platforms/'),
                        U1.UrlExportStart-(U1.UrlPlatformStart+LEN('/Platforms/'))
                    )
            END AS UrlPolicyID
    ) UP
),
BlockPolicy AS (
    SELECT
        PolicyBlockID,
        COALESCE(
            MAX(
                CASE
                    WHEN [Message] LIKE 'Exporting policy ZIP for platform "%'
                        THEN ParsedPolicyID
                END
            ),
            MAX(ParsedPolicyID)
        ) AS PolicyID
    FROM ParsedLogs
    GROUP BY PolicyBlockID
),
LogFindings AS (
    SELECT
        BP.PolicyID,
        MAX(PL.HasHttp400) AS HasHttp400,
        MAX(PL.HasBadRequest) AS HasBadRequest,
        MAX(PL.HasNoZip) AS HasNoZip,
        MIN(
            CASE
                WHEN PL.HasHttp400=1
                  OR PL.HasBadRequest=1
                  OR PL.HasNoZip=1
                    THEN PL.Id
            END
        ) AS FirstIssueLogId,
        MIN(
            CASE
                WHEN PL.HasHttp400=1
                  OR PL.HasBadRequest=1
                  OR PL.HasNoZip=1
                    THEN PL.[TimeStamp]
            END
        ) AS FirstIssueTimeStamp
    FROM BlockPolicy BP
    INNER JOIN ParsedLogs PL
        ON PL.PolicyBlockID=BP.PolicyBlockID
    WHERE BP.PolicyID IS NOT NULL
    GROUP BY BP.PolicyID
),
FinalClassification AS (
    SELECT
        EP.PolicyID,
        COALESCE(
            PS.IsDeletionCandidate,
            'NOT_FOUND_IN_CA_EPV_REPORTING'
        ) AS IsDeletionCandidate,

        CFF.CSharpFilteredPolicyID,
        CFF.IsCSharpFilterMutation,
        CFF.CSharpFilterReason,

        COALESCE(LF.HasHttp400,0) AS HasHttp400,
        COALESCE(LF.HasBadRequest,0) AS HasBadRequest,
        COALESCE(LF.HasNoZip,0) AS HasNoZip,

        LF.FirstIssueLogId,
        LF.FirstIssueTimeStamp,

        CASE
            WHEN CFF.IsCSharpFilterMutation=1
             AND PS.IsDeletionCandidate IN ('YES','YES+')
             AND (
                    COALESCE(LF.HasHttp400,0)=1
                 OR COALESCE(LF.HasBadRequest,0)=1
                 OR COALESCE(LF.HasNoZip,0)=1
             )
                THEN 'SPECIAL_CHARACTER_MUTATION_AND_DELETED_AND_LOG_ISSUE'

            WHEN CFF.IsCSharpFilterMutation=1
             AND PS.IsDeletionCandidate IN ('YES','YES+')
                THEN 'SPECIAL_CHARACTER_MUTATION_AND_DELETED'

            WHEN CFF.IsCSharpFilterMutation=1
             AND (
                    COALESCE(LF.HasHttp400,0)=1
                 OR COALESCE(LF.HasBadRequest,0)=1
                 OR COALESCE(LF.HasNoZip,0)=1
             )
                THEN 'SPECIAL_CHARACTER_MUTATION_AND_NO_ZIP_OR_400_LOG_ISSUE'

            WHEN CFF.IsCSharpFilterMutation=1
                THEN 'SPECIAL_CHARACTER_MUTATION'

            WHEN PS.IsDeletionCandidate IN ('YES','YES+')
             AND (
                    COALESCE(LF.HasHttp400,0)=1
                 OR COALESCE(LF.HasBadRequest,0)=1
                 OR COALESCE(LF.HasNoZip,0)=1
             )
                THEN 'DELETED_AND_LOG_ISSUE'

            WHEN PS.IsDeletionCandidate IN ('YES','YES+')
                THEN 'DELETED'

            WHEN COALESCE(LF.HasHttp400,0)=1
              OR COALESCE(LF.HasBadRequest,0)=1
              OR COALESCE(LF.HasNoZip,0)=1
                THEN 'NO_ZIP_OR_400_LOG_ISSUE'

            ELSE 'DUE TO OTHER REASON'
        END AS ExclusionReason

    FROM ExcludedPolicies EP
    LEFT JOIN PolicyState PS
        ON PS.PolicyID=EP.PolicyID
    LEFT JOIN LogFindings LF
        ON LF.PolicyID=EP.PolicyID
    LEFT JOIN CSharpFilterFindings CFF
        ON CFF.PolicyID=EP.PolicyID
)

SELECT
    PolicyID,
    ExclusionReason
FROM FinalClassification
ORDER BY
    CASE
        WHEN ExclusionReason='DUE TO OTHER REASON' THEN 2
        WHEN IsCSharpFilterMutation=1 THEN 0
        ELSE 1
    END,
    PolicyID;
