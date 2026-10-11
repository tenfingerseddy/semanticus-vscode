// What FabricSchemaToSpec gave BEFORE it became a call into the structure manifest (recorded at 90716ced, m11/join head).
// The J8 import draft is kept whole so a difference reads as a diff; the other shapes are kept as the SHA-256 of the same
// serialisation (indented, nulls left out, line ends as \n), which is enough to say "the same bytes". Nothing here may be
// regenerated from the manifest: a golden worked out by the code under test proves nothing.
namespace Semanticus.Tests
{
    internal static class StructureManifestGolden
    {
        internal static readonly (string Shape, string Sha256)[] Hashes =
        {
            ("j8-onboarding-date", "fd8a0909bbc81c3391d9d62c35c54b440c4aeea37662ddd4134ff93dc40f95e5"),
            ("diamond", "d51dd29a46cc2fd5470055fe686b6d47a026696b9754b3b47d17489ea0509cd7"),
            ("galaxy", "42c0cf0197aa7f9d26847cb732c56afca3fac8ac5e3652c15deb15b86d0fbb9f"),
            ("role-playing-declared-backwards", "ecd082e398c416b6abd371a612d96b22160434968fbac1789c0a1d8ed2527135"),
            ("j8-credit-note", "43b5efb601abe55468662b155fefb61a36d4a7ebd389593719757980f412ddc9"),
            ("order-replaces-order", "a044af31493416cd0c00a9d01b463410e5e817a9c623acda65d502ad1a911682"),
            ("diamond-no-fact-key-to-geography", "a9ffb584c834ba4a994ae39fc0b66a00c5d44db030dde7de4357730c6b2fc7ab"),
            ("dimension-with-two-dates", "2967aedd601528fdbff752eba793e1e930d9c2fea168ac98e5f7ffeb7a5e878f"),
        };

        internal const string J8DirectLakeSha256 = "e5f05aa4b2a08abb0d9c2e1413eb723ae8e51b6f2a17d73cb7429cd8be2f687e";

        internal const string J8Import = """"
            {
              "Name": "DW",
              "CompatibilityLevel": 1604,
              "StorageMode": "import",
              "Source": {
                "Kind": "fabric-sql",
                "Server": "x",
                "Database": "DW"
              },
              "Tables": [
                {
                  "Name": "ref_codes",
                  "Role": "isolated",
                  "Entity": "ref_codes",
                  "Schema": "dbo",
                  "Columns": [
                    {
                      "Name": "CODE_TYPE",
                      "DataType": "String",
                      "SourceColumn": "CODE_TYPE",
                      "IsKey": true,
                      "Hidden": true,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "CODE",
                      "DataType": "String",
                      "SourceColumn": "CODE",
                      "IsKey": true,
                      "Hidden": true,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "CODE_DESC",
                      "DataType": "String",
                      "SourceColumn": "CODE_DESC",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    }
                  ]
                },
                {
                  "Name": "dim_date",
                  "Role": "date",
                  "Entity": "dim_date",
                  "Schema": "dbo",
                  "Columns": [
                    {
                      "Name": "DATE_KEY",
                      "DataType": "Int64",
                      "SourceColumn": "DATE_KEY",
                      "IsKey": false,
                      "Hidden": true,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "CAL_DT",
                      "DataType": "DateTime",
                      "SourceColumn": "CAL_DT",
                      "IsKey": true,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "FY_NO",
                      "DataType": "Int64",
                      "SourceColumn": "FY_NO",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "FY_QTR",
                      "DataType": "Int64",
                      "SourceColumn": "FY_QTR",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "MTH_NO",
                      "DataType": "Int64",
                      "SourceColumn": "MTH_NO",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "MTH_ABBR",
                      "DataType": "String",
                      "SourceColumn": "MTH_ABBR",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "DOW_NO",
                      "DataType": "Int64",
                      "SourceColumn": "DOW_NO",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "WKDAY_FLG",
                      "DataType": "String",
                      "SourceColumn": "WKDAY_FLG",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    }
                  ]
                },
                {
                  "Name": "dim_client",
                  "Role": "dimension",
                  "Entity": "dim_client",
                  "Schema": "dbo",
                  "Columns": [
                    {
                      "Name": "CLNT_ID",
                      "DataType": "String",
                      "SourceColumn": "CLNT_ID",
                      "IsKey": true,
                      "Hidden": true,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "CLNT_NM",
                      "DataType": "String",
                      "SourceColumn": "CLNT_NM",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "IND_CD",
                      "DataType": "String",
                      "SourceColumn": "IND_CD",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "RGN_CD",
                      "DataType": "String",
                      "SourceColumn": "RGN_CD",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "ACTV_FLG",
                      "DataType": "String",
                      "SourceColumn": "ACTV_FLG",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "ONBRD_DT_KEY",
                      "DataType": "Int64",
                      "SourceColumn": "ONBRD_DT_KEY",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    }
                  ]
                },
                {
                  "Name": "dim_staff",
                  "Role": "dimension",
                  "Entity": "dim_staff",
                  "Schema": "dbo",
                  "Columns": [
                    {
                      "Name": "EMP_NO",
                      "DataType": "String",
                      "SourceColumn": "EMP_NO",
                      "IsKey": true,
                      "Hidden": true,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "EMP_NM",
                      "DataType": "String",
                      "SourceColumn": "EMP_NM",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "GRADE_CD",
                      "DataType": "String",
                      "SourceColumn": "GRADE_CD",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "PRAC_CD",
                      "DataType": "String",
                      "SourceColumn": "PRAC_CD",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "STD_RATE",
                      "DataType": "Decimal",
                      "SourceColumn": "STD_RATE",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "HIRE_DT_KEY",
                      "DataType": "Int64",
                      "SourceColumn": "HIRE_DT_KEY",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "TERM_FLG",
                      "DataType": "String",
                      "SourceColumn": "TERM_FLG",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    }
                  ]
                },
                {
                  "Name": "dim_project",
                  "Role": "dimension",
                  "Entity": "dim_project",
                  "Schema": "dbo",
                  "Columns": [
                    {
                      "Name": "PRJ_ID",
                      "DataType": "String",
                      "SourceColumn": "PRJ_ID",
                      "IsKey": true,
                      "Hidden": true,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "CLNT_ID",
                      "DataType": "String",
                      "SourceColumn": "CLNT_ID",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "PRJ_NM",
                      "DataType": "String",
                      "SourceColumn": "PRJ_NM",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "PRAC_CD",
                      "DataType": "String",
                      "SourceColumn": "PRAC_CD",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "ENG_TYPE_CD",
                      "DataType": "String",
                      "SourceColumn": "ENG_TYPE_CD",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "BUDGET_HRS",
                      "DataType": "Int64",
                      "SourceColumn": "BUDGET_HRS",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "BUDGET_AMT",
                      "DataType": "Decimal",
                      "SourceColumn": "BUDGET_AMT",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "START_DT_KEY",
                      "DataType": "Int64",
                      "SourceColumn": "START_DT_KEY",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "END_DT_KEY",
                      "DataType": "Int64",
                      "SourceColumn": "END_DT_KEY",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "STAT_CD",
                      "DataType": "String",
                      "SourceColumn": "STAT_CD",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    }
                  ]
                },
                {
                  "Name": "fact_timesheet",
                  "Role": "fact",
                  "Entity": "fact_timesheet",
                  "Schema": "dbo",
                  "Columns": [
                    {
                      "Name": "TS_LINE_ID",
                      "DataType": "String",
                      "SourceColumn": "TS_LINE_ID",
                      "IsKey": true,
                      "Hidden": true,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "EMP_NO",
                      "DataType": "String",
                      "SourceColumn": "EMP_NO",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "PRJ_ID",
                      "DataType": "String",
                      "SourceColumn": "PRJ_ID",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "WORK_DT_KEY",
                      "DataType": "Int64",
                      "SourceColumn": "WORK_DT_KEY",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "HRS",
                      "DataType": "Decimal",
                      "SourceColumn": "HRS",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "BILL_FLG",
                      "DataType": "String",
                      "SourceColumn": "BILL_FLG",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "BILL_RATE",
                      "DataType": "Decimal",
                      "SourceColumn": "BILL_RATE",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "APPR_FLG",
                      "DataType": "String",
                      "SourceColumn": "APPR_FLG",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "SRC_ROW_TXT",
                      "DataType": "String",
                      "SourceColumn": "SRC_ROW_TXT",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    }
                  ]
                },
                {
                  "Name": "fact_invoice",
                  "Role": "fact",
                  "Entity": "fact_invoice",
                  "Schema": "dbo",
                  "Columns": [
                    {
                      "Name": "INV_NO",
                      "DataType": "String",
                      "SourceColumn": "INV_NO",
                      "IsKey": true,
                      "Hidden": true,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "PRJ_ID",
                      "DataType": "String",
                      "SourceColumn": "PRJ_ID",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "CLNT_ID",
                      "DataType": "String",
                      "SourceColumn": "CLNT_ID",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "INV_DT_KEY",
                      "DataType": "Int64",
                      "SourceColumn": "INV_DT_KEY",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "DUE_DT_KEY",
                      "DataType": "Int64",
                      "SourceColumn": "DUE_DT_KEY",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "PAID_DT_KEY",
                      "DataType": "Int64",
                      "SourceColumn": "PAID_DT_KEY",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "INV_AMT",
                      "DataType": "Decimal",
                      "SourceColumn": "INV_AMT",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "TAX_AMT",
                      "DataType": "Decimal",
                      "SourceColumn": "TAX_AMT",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "Sum"
                    },
                    {
                      "Name": "CURR_CD",
                      "DataType": "String",
                      "SourceColumn": "CURR_CD",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    },
                    {
                      "Name": "VOID_FLG",
                      "DataType": "String",
                      "SourceColumn": "VOID_FLG",
                      "IsKey": false,
                      "Hidden": false,
                      "SummarizeBy": "None"
                    }
                  ]
                }
              ],
              "Relationships": [
                {
                  "FromTable": "fact_timesheet",
                  "FromColumn": "EMP_NO",
                  "ToTable": "dim_staff",
                  "ToColumn": "EMP_NO"
                },
                {
                  "FromTable": "fact_timesheet",
                  "FromColumn": "PRJ_ID",
                  "ToTable": "dim_project",
                  "ToColumn": "PRJ_ID"
                },
                {
                  "FromTable": "fact_timesheet",
                  "FromColumn": "WORK_DT_KEY",
                  "ToTable": "dim_date",
                  "ToColumn": "DATE_KEY"
                },
                {
                  "FromTable": "dim_project",
                  "FromColumn": "CLNT_ID",
                  "ToTable": "dim_client",
                  "ToColumn": "CLNT_ID",
                  "IsActive": false,
                  "Note": "Off because fact_invoice already reaches dim_client on its own link, and this link would give it a second path. Two paths between the same tables make filters ambiguous, so only one can be on, and fact_invoice\u0027s own links are the ones kept on. A measure can still use this one with USERELATIONSHIP."
                },
                {
                  "FromTable": "fact_invoice",
                  "FromColumn": "PRJ_ID",
                  "ToTable": "dim_project",
                  "ToColumn": "PRJ_ID"
                },
                {
                  "FromTable": "fact_invoice",
                  "FromColumn": "INV_DT_KEY",
                  "ToTable": "dim_date",
                  "ToColumn": "DATE_KEY"
                },
                {
                  "FromTable": "fact_invoice",
                  "FromColumn": "CLNT_ID",
                  "ToTable": "dim_client",
                  "ToColumn": "CLNT_ID"
                },
                {
                  "FromTable": "fact_invoice",
                  "FromColumn": "DUE_DT_KEY",
                  "ToTable": "dim_date",
                  "ToColumn": "DATE_KEY",
                  "IsActive": false,
                  "Note": "Off because fact_invoice already has an active link to dim_date, on INV_DT_KEY. Only one link between two tables can be on. A measure can still use this one with USERELATIONSHIP."
                },
                {
                  "FromTable": "fact_invoice",
                  "FromColumn": "PAID_DT_KEY",
                  "ToTable": "dim_date",
                  "ToColumn": "DATE_KEY",
                  "IsActive": false,
                  "Note": "Off because fact_invoice already has an active link to dim_date, on INV_DT_KEY. Only one link between two tables can be on. A measure can still use this one with USERELATIONSHIP."
                }
              ],
              "Measures": [
                {
                  "Table": "fact_timesheet",
                  "Name": "Total HRS",
                  "Dax": "SUM ( \u0027fact_timesheet\u0027[HRS] )",
                  "DisplayFolder": "Base Measures"
                },
                {
                  "Table": "fact_invoice",
                  "Name": "Total INV_AMT",
                  "Dax": "SUM ( \u0027fact_invoice\u0027[INV_AMT] )",
                  "DisplayFolder": "Base Measures"
                },
                {
                  "Table": "fact_invoice",
                  "Name": "Total TAX_AMT",
                  "Dax": "SUM ( \u0027fact_invoice\u0027[TAX_AMT] )",
                  "DisplayFolder": "Base Measures"
                }
              ],
              "TimeIntelligence": [
                "YTD",
                "QTD",
                "MTD",
                "PY",
                "YoY",
                "YoYPct"
              ],
              "TimeIntelligenceBaseMeasures": [
                "Total HRS",
                "Total INV_AMT",
                "Total TAX_AMT"
              ],
              "DateTable": {
                "Name": "dim_date",
                "DateColumn": "CAL_DT",
                "MarkAsDate": true
              }
            }
            """";
    }
}
