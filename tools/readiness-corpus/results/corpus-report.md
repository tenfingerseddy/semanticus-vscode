# AI-readiness: the public-model corpus scan

Scanned **40 public semantic models** (offline, metadata only) with the Semanticus
AI-readiness analyzer. Corpus, commits and raw scores are committed beside this file;
anyone can re-run it with `node scan.mjs fetch && scan && report`.

**Median score: 54.1 (grade F)**

| Grade | Models |
|---|---|
| B | 1 |
| C | 2 |
| D | 9 |
| F | 28 |

## Disclosed bias
Public repos skew toward samples and teaching material, which are typically CLEANER than
production client models. If anything, this understates the real-world problem.

## Per-model

| Model | Kind | Grade | Score |
|---|---|---|---|
| DaniBunny/Fabric-DE-CICD | training | F | 39 |
| CSCfi/antero | community | F | 39.1 |
| microsoft/fabric-samples | microsoft-sample | F | 41.4 |
| tomatminceddata/PBIR_XRAY | community | F | 43.1 |
| microsoft/Analysis-Services | microsoft-sample | F | 43.3 |
| Cyberlorians/nistframework | community | F | 43.3 |
| MeteoWatch/MeteoWatch | community | F | 45.5 |
| microsoft/fabric-racing-sim | microsoft-sample | F | 46.2 |
| Azure/tech-debt-analytics | microsoft-sample | F | 46.6 |
| kevchant/GitHub-FUAM-Deploymenator | community | F | 46.9 |
| miguelASL/Eurocopa_Espana | community | F | 47.2 |
| Mike-Honey/covid-19-au-vaccinations | community | F | 47.6 |
| ecotte/Fabric-Monitoring-RTI | community | F | 48.3 |
| bcgov/moh-APO-Reporting | community | F | 49.5 |
| ayodejiayodele/github-developer-metrics | community | F | 51.1 |
| kerski/fabric-dataops-patterns | training | F | 51.2 |
| Open-Education-AI/OEAI | community | F | 51.3 |
| sonbaoharryson/Data_Engineer_JobPulse_Project | community | F | 52.5 |
| djouallah/aemo_fabric | community | F | 53.4 |
| microsoft/PowerBI-LogAnalytics-Template-Reports | microsoft-sample | F | 53.6 |
| FHaurum/FHSQLMonitor | community | F | 54.1 |
| PacktPublishing/Microsoft-Power-BI-Cookbook | training | F | 54.9 |
| alisonpezzott/reactor-pbi-maio-25 | training | F | 55.3 |
| aditiv101/Youtube_analytics_dashboard | community | F | 56.2 |
| ProdataSQL/FinancialModelling | community | F | 56.4 |
| CareTogether/CareTogether-PowerBI | community | F | 57.6 |
| PBI-DataVizzle/pbi_content | community | F | 58.6 |
| DataChant/Trello-Power-BI | community | F | 59.9 |
| alisonpezzott/pbi-docs | community | D | 60 |
| jurgenfolz/WorldDataReport | community | D | 61.4 |
| NelsonNeba/Workforce-Hiring-Optimization-Dashboard- | community | D | 61.8 |
| stephbruno/Power-BI-Accessibility-Checker | community | D | 63.1 |
| vlpatkosdani/powerbi-cicd-with-githubactions-demos | community | D | 63.5 |
| jurgenfolz/Stock-Intelligence | community | D | 67 |
| RuiRomano/fabric-cli-powerbi-cicd-sample | community | D | 68.2 |
| pbi-tools/sales-sample | community | D | 69 |
| jeremypj/budget-intelligence-ynab | community | D | 69 |
| Rede-DSBR/DocPBI2 | community | C | 70.3 |
| jeremypj/Power-BI-for-BigTime | community | C | 72.6 |
| data-goblin/power-bi-visual-templates | community | B | 81.5 |

## Not scannable (1)

- AllanYiin/TabularModelBook: open_model: open_model failed: Unable to load Tabular Model (Compatibility Level 1200+) from C:\Users\KaneSnyder(nexwave)\Semanticus\tools\readiness-corpus\work\AllanYiin__TabularModelBook\Ch08\Ch08\M
