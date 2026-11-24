// The same shapes as CICDPipelineGenerator.Domain (enums travel as camelCase text).
export type Platform = 'gitHubActions' | 'azureDevOps' | 'gitLab' | 'jenkins' | 'bitbucket' | 'circleCi' | 'travisCi' | 'cloudBuild' | 'codePipeline' | 'teamCity' | 'argoWorkflows';
export type Stack = 'dotNet' | 'node' | 'python' | 'java' | 'none' | 'dotNetFramework' | 'gradle' | 'go' | 'ruby' | 'php' | 'rust' | 'android' | 'ios' | 'msix' | 'cpp' | 'swift' | 'scala' | 'elixir' | 'flutter';
export type BuildAgent = 'hostedLinux' | 'hostedWindows' | 'selfHostedLinux' | 'selfHostedWindows' | 'hostedMac' | 'selfHostedMac';
export type MigrationKind = 'none' | 'efCoreBundle' | 'sqlScripts' | 'dacpac';
export type Target = 'appService' | 'functionApp' | 'staticWebApp' | 'containerApp' | 'aks' | 'terraform' | 'bicep' | 'windowsService' | 'iis' | 'scheduledTask' | 'linuxService' | 'package' | 'awsS3' | 'awsLambda' | 'awsElasticBeanstalk' | 'awsEcs' | 'gcpCloudRun' | 'gcpCloudFunctions' | 'gcpAppEngine' | 'gcpGke' | 'script' | 'awsEks' | 'kubernetes' | 'containerRegistry' | 'armTemplate' | 'cloudFormation' | 'awsCdk' | 'pulumi' | 'googlePlay' | 'appStore' | 'microsoftStore' | 'netlify' | 'vercel' | 'cloudflarePages' | 'firebaseHosting' | 'awsSam' | 'awsAppRunner' | 'gitOps' | 'digitalOcean' | 'database' | 'apiManagement' | 'logicApp' | 'dataFactory' | 'heroku' | 'serviceFabric';
export type MobileFramework = 'native' | 'reactNative' | 'maui';
export type CloudProvider = 'azure' | 'aws' | 'gcp';
export type DeployStrategy = 'standard' | 'blueGreen' | 'canary';
export type RegistryKind = 'dockerHub' | 'gitHub' | 'other';
export type Flow = 'promotion' | 'branchPerEnvironment' | 'tag';
export type HostOs = 'linux' | 'windows';
export type FeedType = 'nuGet' | 'npm' | 'maven' | 'pip' | 'go' | 'rubyGems' | 'composer' | 'cargo' | 'hex' | 'pub' | 'swiftRegistry' | 'conan';
export type FeedKind = 'azureArtifacts' | 'gitHubPackages' | 'jFrog' | 'other';

export type ServiceKind = 'postgres' | 'mySql' | 'sqlServer' | 'redis' | 'mongoDb';

export const SERVICES: { value: ServiceKind; label: string }[] = [
  { value: 'postgres', label: 'PostgreSQL' },
  { value: 'mySql', label: 'MySQL' },
  { value: 'sqlServer', label: 'SQL Server' },
  { value: 'redis', label: 'Redis' },
  { value: 'mongoDb', label: 'MongoDB' },
];

export interface PipelineSpec {
  name: string;
  repository: string | null;
  platforms: Platform[];
  build: BuildSettings;
  deploy: DeploySettings;
  flow: Flow;
  mainBranch: string;
  buildPullRequests: boolean;
  manualRun: boolean;
  schedule: string | null;
  tagPattern: string;
  notifyOnFailure: boolean;
  notifyOnSuccess: boolean;
  createRelease: boolean;
  severalApps: boolean;
  pathFilters: string[];
  setupScript: boolean;
  templateRepository: string | null;
  templateRef: string;
  environments: EnvironmentSpec[];
}

export interface BuildSettings {
  stack: Stack;
  version: string | null;
  workingDirectory: string;
  projectPath: string | null;
  testPath: string | null;
  runTests: boolean;
  outputFolder: string;
  customCommands: string[];
  publishTestResults: boolean;
  auditDependencies: boolean;
  cachePackages: boolean;
  coverage: boolean;
  sonarProjectKey: string | null;
  codeScan: boolean;
  codeQl: boolean;
  lint: boolean;
  securityScan: boolean;
  sbom: boolean;
  services: ServiceKind[];
  alsoTestWith: string[];
  mobileFramework: MobileFramework;
  scheme: string | null;
  exportOptions: string;
  agent: BuildAgent;
  agentLabel: string | null;
  packageFeeds: PackageFeed[];
}

// A private NuGet feed or npm registry; its credentials are secrets of the pipeline system, never settings.
export interface PackageFeed {
  name: string;
  type: FeedType;
  kind: FeedKind;
  organization: string | null;
  project: string | null;
  feed: string | null;
  url: string | null;
  scope: string | null;
  inRepositoryConfig: boolean;
  jFrogOidcProvider: string | null;
}

export interface DeploySettings {
  target: Target;
  os: HostOs;
  registry: string | null;
  imageName: string | null;
  dockerfile: string;
  dockerContext: string;
  namespace: string;
  manifests: string;
  deploymentName: string | null;
  containerName: string | null;
  entraIdCluster: boolean;
  appId: string | null;
  signImage: boolean;
  submitForReview: boolean;
  gitOpsRepository: string | null;
  registryKind: RegistryKind;
  helmChart: string | null;
  infraDirectory: string;
  terraformVersion: string;
  stateResourceGroup: string | null;
  stateStorageAccount: string | null;
  stateContainer: string;
  bicepTemplate: string;
  infraCloud: CloudProvider;
  strategy: DeployStrategy;
  canaryPercent: number;
  canaryMinutes: number;
  preview: boolean;
  keepOnServer: string[];
  stopTimeoutSeconds: number;
  serviceExecutable: string | null;
  deployCommands: string[];
  scriptOnServer: boolean;
  scriptSignsInToAzure: boolean;
  settingsFile: string;
  migrations: MigrationKind;
  migrationsProject: string | null;
  migrationsFolder: string;
  dacpacPath: string | null;
  cloudRegion: string | null;
  bucket: string | null;
  functionRuntime: string | null;
  functionEntryPoint: string | null;
  appYaml: string;
  rollbackPipeline: boolean;
  previewPullRequests: boolean;
  fabricProject: string | null;
  remote: boolean;
  publishFeed: PackageFeed | null;
  sqlTrustedConnection: boolean;
}

export interface EnvironmentSpec {
  name: string;
  branch: string;
  requireApproval: boolean;
  resourceGroup: string | null;
  resourceName: string | null;
  slot: string | null;
  serviceConnection: string | null;
  parametersFile: string | null;
  serverLabel: string | null;
  targetPath: string | null;
  appPool: string | null;
  healthCheckUrl: string | null;
  deployWindow: string | null;
  endToEndFolder: string | null;
  loadTestScript: string | null;
  beforeDeploy: string[];
  afterDeploy: string[];
  servers: string[];
  hosts: string[];
  settings: EnvironmentSetting[];
}

// A value of an environment: written here, or the name of a secret of the pipeline system.
export interface EnvironmentSetting {
  name: string;
  value: string | null;
  secret: string | null;
}

export interface Message {
  severity: 'error' | 'warning';
  text: string;
}

export interface GeneratedFile {
  path: string;
  content: string;
}

export interface GenerationResult {
  files: GeneratedFile[];
  messages: Message[];
}

export interface Option<T> {
  value: T;
  label: string;
  hint: string;
}

export const PLATFORMS: Option<Platform>[] = [
  { value: 'gitHubActions', label: 'GitHub Actions', hint: '.github/workflows/deploy.yml' },
  { value: 'azureDevOps', label: 'Azure DevOps', hint: 'azure-pipelines.yml' },
  { value: 'gitLab', label: 'GitLab CI/CD', hint: '.gitlab-ci.yml' },
  { value: 'jenkins', label: 'Jenkins', hint: 'Jenkinsfile' },
  { value: 'bitbucket', label: 'Bitbucket Pipelines', hint: 'bitbucket-pipelines.yml' },
  { value: 'circleCi', label: 'CircleCI', hint: '.circleci/config.yml' },
  { value: 'travisCi', label: 'Travis CI', hint: '.travis.yml' },
  { value: 'cloudBuild', label: 'Google Cloud Build', hint: 'cloudbuild.yaml' },
  { value: 'codePipeline', label: 'AWS CodePipeline', hint: 'buildspec.yml (CodeBuild)' },
  { value: 'teamCity', label: 'TeamCity', hint: '.teamcity/settings.kts' },
  { value: 'argoWorkflows', label: 'Argo Workflows', hint: 'argo/workflow-template.yaml' },
];

export const TARGETS: Option<Target>[] = [
  { value: 'appService', label: 'Azure App Service', hint: 'A web app or API; the build is uploaded to the app.' },
  { value: 'functionApp', label: 'Azure Functions', hint: 'A function app; the build is uploaded as a zip.' },
  { value: 'staticWebApp', label: 'Azure Static Web Apps', hint: 'A static site (React, Angular, Vue); signs in with a deployment token.' },
  { value: 'containerApp', label: 'Azure Container Apps', hint: 'The Dockerfile is built in Azure Container Registry and the app gets the new image.' },
  { value: 'aks', label: 'Azure Kubernetes Service', hint: 'The image is built in the registry, the manifests applied and the deployment updated.' },
  { value: 'terraform', label: 'Terraform (infrastructure)', hint: 'Checks the code, then plans and applies each environment with its own state.' },
  { value: 'bicep', label: 'Bicep (infrastructure)', hint: 'Checks the template, then previews (what-if) and deploys into each resource group.' },
  { value: 'armTemplate', label: 'ARM template (infrastructure)', hint: 'Reads the JSON template, then previews (what-if) and deploys into each resource group.' },
  { value: 'cloudFormation', label: 'AWS CloudFormation (infrastructure)', hint: 'Checks the template with cfn-lint, shows the change set and deploys one stack per environment.' },
  { value: 'awsCdk', label: 'AWS CDK (infrastructure)', hint: 'Synthesises the application, shows the diff and deploys each environment\'s stack.' },
  { value: 'pulumi', label: 'Pulumi (infrastructure)', hint: 'Previews and updates one Pulumi stack per environment, on Azure, AWS or Google Cloud.' },
  { value: 'windowsService', label: 'Windows service (your servers)', hint: 'On each server: stops the service, keeps the current version, copies the new one, starts it, checks it and rolls back on failure.' },
  { value: 'iis', label: 'IIS site (your servers)', hint: 'On each server: stops the application pool, keeps the current version, copies the new one, starts it, checks it and rolls back on failure.' },
  { value: 'linuxService', label: 'Linux service (your servers)', hint: 'On each server: stops the systemd service, keeps the current version, copies the new one with rsync, starts it, checks it and rolls back on failure.' },
  { value: 'scheduledTask', label: 'Scheduled task (your servers)', hint: 'On each server: disables and stops the task, keeps the current version, copies the new one and enables the task again.' },
  { value: 'package', label: 'Publish a package (NuGet or npm)', hint: 'Packs the project and publishes the package to a feed.' },
  { value: 'awsS3', label: 'AWS S3 (static site)', hint: 'Synchronises the build with the bucket.' },
  { value: 'awsLambda', label: 'AWS Lambda', hint: 'Uploads the build as the code of the function and waits until it is active.' },
  { value: 'awsElasticBeanstalk', label: 'AWS Elastic Beanstalk', hint: 'Uploads the build as a new application version and moves the environment to it.' },
  { value: 'awsEcs', label: 'AWS ECS', hint: 'Builds the image, pushes it to ECR and rolls the service to a new task definition.' },
  { value: 'awsEks', label: 'AWS EKS (Kubernetes)', hint: 'Builds the image, pushes it to ECR and updates the deployment of the cluster (manifests or a Helm chart).' },
  { value: 'gcpCloudRun', label: 'Google Cloud Run', hint: 'Builds the image with Cloud Build and deploys it to the service.' },
  { value: 'gcpCloudFunctions', label: 'Google Cloud Functions', hint: 'Deploys the function from its source.' },
  { value: 'gcpAppEngine', label: 'Google App Engine', hint: 'Deploys the application described by app.yaml.' },
  { value: 'gcpGke', label: 'Google Kubernetes Engine', hint: 'Builds the image with Cloud Build, applies the manifests and updates the deployment.' },
  { value: 'kubernetes', label: 'Kubernetes (any cluster)', hint: 'Builds the image, pushes it to Docker Hub, GitHub or another registry, and updates the cluster reached with its kubeconfig (manifests or a Helm chart).' },
  { value: 'containerRegistry', label: 'Container image (Docker Hub, GitHub, other registry)', hint: 'Builds the image and pushes it; each environment gives it its own tag.' },
  { value: 'googlePlay', label: 'Google Play (Android app)', hint: 'Uploads the signed bundle to the first environment\'s track and promotes it to the next ones.' },
  { value: 'appStore', label: 'TestFlight (iOS app)', hint: 'Uploads the .ipa to TestFlight; each environment is a group of testers that gets the build.' },
  { value: 'microsoftStore', label: 'Microsoft Store (MSIX)', hint: 'Publishes the package to a flight per environment, or to everyone.' },
  { value: 'netlify', label: 'Netlify (site)', hint: 'Uploads the built site to each environment\'s site.' },
  { value: 'vercel', label: 'Vercel (site)', hint: 'Deploys the built site to each environment\'s project.' },
  { value: 'cloudflarePages', label: 'Cloudflare Pages (site)', hint: 'Uploads the built site to each environment\'s project.' },
  { value: 'firebaseHosting', label: 'Firebase Hosting (site)', hint: 'Deploys the site described by firebase.json to each environment\'s project.' },
  { value: 'database', label: 'Only the database', hint: 'Runs the migrations of each environment and deploys nothing else.' },
  { value: 'apiManagement', label: 'Azure API Management', hint: 'Imports the API from its OpenAPI file.' },
  { value: 'logicApp', label: 'Azure Logic Apps (Standard)', hint: 'Uploads the workflows folder as a zip.' },
  { value: 'dataFactory', label: 'Azure Data Factory', hint: 'Deploys the ARM template the factory publishes into each environment\'s factory.' },
  { value: 'serviceFabric', label: 'Azure Service Fabric', hint: 'Uploads the application package to each cluster and creates or upgrades the application.' },
  { value: 'heroku', label: 'Heroku (container)', hint: 'Builds the Dockerfile, pushes it to Heroku\'s registry and releases it.' },
  { value: 'digitalOcean', label: 'DigitalOcean App Platform', hint: 'Starts the deployment of the app, which builds from the repository, and waits for it.' },
  { value: 'awsSam', label: 'AWS SAM (serverless application)', hint: 'Builds the application with sam build and deploys one stack per environment.' },
  { value: 'awsAppRunner', label: 'AWS App Runner', hint: 'Builds the image, pushes it to ECR and moves the service to it.' },
  { value: 'gitOps', label: 'GitOps (Argo CD or Flux)', hint: 'Pushes the image and writes its tag into the manifests repository the cluster follows.' },
  { value: 'script', label: 'Your own deploy commands', hint: 'Your commands run per environment: bash on a hosted runner, or PowerShell on the environment\'s servers.' },
];

export const STACKS: Option<Stack>[] = [
  { value: 'dotNet', label: '.NET', hint: 'restore, build, test, publish' },
  { value: 'node', label: 'Node.js', hint: 'npm ci, npm run build, npm test' },
  { value: 'python', label: 'Python', hint: 'pip install -r requirements.txt, pytest' },
  { value: 'java', label: 'Java (Maven)', hint: 'mvn package' },
  { value: 'gradle', label: 'Java or Kotlin (Gradle)', hint: 'gradlew build' },
  { value: 'go', label: 'Go', hint: 'go vet, go build, go test' },
  { value: 'ruby', label: 'Ruby', hint: 'bundle install, bundle exec rake' },
  { value: 'php', label: 'PHP', hint: 'composer install, phpunit' },
  { value: 'rust', label: 'Rust', hint: 'cargo build --release, cargo test' },
  { value: 'cpp', label: 'C or C++ (CMake)', hint: 'cmake, ctest, cmake --install' },
  { value: 'swift', label: 'Swift (server)', hint: 'swift build -c release, swift test' },
  { value: 'scala', label: 'Scala (sbt)', hint: 'sbt test package' },
  { value: 'elixir', label: 'Elixir', hint: 'mix compile, mix test, mix release' },
  { value: 'flutter', label: 'Flutter (web)', hint: 'flutter analyze, flutter test, flutter build web' },
  { value: 'android', label: 'Android app', hint: 'gradlew test bundleRelease, signed with your keystore' },
  { value: 'ios', label: 'iOS app (macOS agent)', hint: 'xcodebuild test, archive and export' },
  { value: 'msix', label: 'Windows app as MSIX (Windows agent)', hint: 'MSBuild builds the Store package' },
  { value: 'none', label: 'Nothing to compile', hint: 'Only the Dockerfile, or your own commands' },
  { value: 'dotNetFramework', label: '.NET Framework (Windows)', hint: 'MSBuild restore, build and publish; vstest' },
];

export const REGISTRY_KINDS: Option<RegistryKind>[] = [
  { value: 'dockerHub', label: 'Docker Hub', hint: 'user or organisation' },
  { value: 'gitHub', label: 'GitHub Container Registry (ghcr.io)', hint: 'owner' },
  { value: 'other', label: 'Another registry', hint: 'registry.contoso.com/team' },
];

export const AGENTS: Option<BuildAgent>[] = [
  { value: 'hostedLinux', label: 'Hosted Linux', hint: '' },
  { value: 'hostedWindows', label: 'Hosted Windows', hint: '' },
  { value: 'selfHostedLinux', label: 'Your own Linux agents', hint: '' },
  { value: 'selfHostedWindows', label: 'Your own Windows agents', hint: '' },
  { value: 'hostedMac', label: 'Hosted macOS', hint: '' },
  { value: 'selfHostedMac', label: 'Your own macOS agents', hint: '' },
];

export const FLOWS: Option<Flow>[] = [
  { value: 'promotion', label: 'One branch, environment after environment', hint: 'A push deploys to the first environment, then the next, each waiting for the one before (and its approval).' },
  { value: 'tag', label: 'A tag deploys, environment after environment', hint: 'A tag such as v1.2.3 deploys to every environment in order; pushes to the branch only build and test.' },
  { value: 'branchPerEnvironment', label: 'One branch per environment', hint: 'Each environment deploys from its own branch, for example develop to dev and main to prod.' },
];

export const FEED_KINDS: Option<FeedKind>[] = [
  { value: 'azureArtifacts', label: 'Azure Artifacts', hint: 'Azure DevOps signs in with its build identity; GitHub and GitLab use a personal access token.' },
  { value: 'gitHubPackages', label: 'GitHub Packages', hint: 'GitHub Actions uses its own GITHUB_TOKEN; the others use a personal access token.' },
  { value: 'jFrog', label: 'JFrog Artifactory', hint: 'GitHub Actions can sign in with OpenID Connect; the others use an access token.' },
  { value: 'other', label: 'Other (by URL)', hint: 'Any NuGet v3 feed or npm registry: ProGet, Nexus, MyGet...' },
];

export const DEFAULT_VERSIONS: Record<Stack, string> = { dotNet: '10.0', node: '22', python: '3.12', java: '21', none: '', dotNetFramework: '', gradle: '21', go: '1.25', ruby: '3.4', php: '8.4', rust: 'stable', android: '17', ios: '', msix: '', cpp: '', swift: '6.0', scala: '21', elixir: '1.18', flutter: 'stable' };

export const RESOURCE_LABELS: Record<Target, string> = {
  appService: 'Web app',
  functionApp: 'Function app',
  staticWebApp: 'Static web app',
  containerApp: 'Container app',
  aks: 'AKS cluster',
  terraform: '',
  bicep: '',
  windowsService: 'Windows service name',
  iis: 'IIS site name',
  scheduledTask: 'Scheduled task name',
  linuxService: 'systemd service name',
  package: '',
  awsS3: 'S3 bucket',
  awsLambda: 'Lambda function',
  awsElasticBeanstalk: 'Elastic Beanstalk environment',
  awsEcs: 'ECS service',
  gcpCloudRun: 'Cloud Run service',
  gcpCloudFunctions: 'Function',
  gcpAppEngine: '',
  gcpGke: 'GKE cluster',
  awsEks: 'EKS cluster',
  armTemplate: '',
  cloudFormation: 'CloudFormation stack',
  awsCdk: 'CDK stack',
  pulumi: 'Pulumi stack',
  googlePlay: 'Track (internal, alpha, beta, production)',
  appStore: 'TestFlight group (optional)',
  microsoftStore: 'Flight ID (optional)',
  netlify: 'Netlify site',
  vercel: 'Vercel project ID',
  cloudflarePages: 'Pages project',
  firebaseHosting: 'Firebase project',
  awsSam: 'Stack',
  awsAppRunner: 'App Runner service',
  gitOps: '',
  digitalOcean: 'App ID',
  database: '',
  apiManagement: 'API Management service',
  logicApp: 'Logic App',
  dataFactory: 'Data factory',
  heroku: 'Heroku app',
  serviceFabric: 'Service Fabric application',
  kubernetes: '',
  containerRegistry: '',
  script: '',
};

export type ReviewArea = 'project' | 'platforms' | 'build' | 'deploy' | 'environments' | 'triggers';

export interface ReviewItem {
  area: ReviewArea;
  text: string;
}

// What the AI proposed; nothing changes until the person approves it.
export interface PipelineProposal {
  summary: string;
  spec: PipelineSpec;
  changes: string[];
  review: ReviewItem[];
  questions: string[];
  messages: Message[];
}

export interface AiStatus {
  enabled: boolean;
  provider: string;
  model: string;
  problem: string | null;
}

export const AREA_LABELS: Record<ReviewArea, string> = {
  project: 'Project',
  platforms: 'Pipeline systems',
  build: 'Build',
  deploy: 'Deploy to',
  environments: 'Environments',
  triggers: 'When it runs',
};
