import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import {
  AGENTS, REGISTRY_KINDS, AREA_LABELS, AiStatus, DEFAULT_VERSIONS, EnvironmentSetting, FEED_KINDS, FeedKind, FeedType, PackageFeed, PipelineProposal, EnvironmentSpec, FLOWS, GeneratedFile, GenerationResult, PLATFORMS, Platform, PipelineSpec, RESOURCE_LABELS, SERVICES, STACKS, ServiceKind, TARGETS,
} from './models';

const SAVED = 'cicdpipelinegenerator.spec';

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule],
  templateUrl: './app.component.html',
  styleUrl: './app.component.css',
})
export class AppComponent {
  private readonly http = inject(HttpClient);
  private timer: ReturnType<typeof setTimeout> | undefined;

  protected readonly platforms = PLATFORMS;
  protected readonly targets = TARGETS;
  protected readonly stacks = STACKS;
  protected readonly flows = FLOWS;
  protected readonly feedKinds = FEED_KINDS;
  protected readonly agents = AGENTS;
  protected readonly registryKinds = REGISTRY_KINDS;
  protected readonly defaultVersions = DEFAULT_VERSIONS;
  protected readonly resourceLabels = RESOURCE_LABELS;

  // The spec is edited in place by the form; every change bumps the revision and asks for the YAML again.
  protected spec!: PipelineSpec;
  protected readonly revision = signal(0);
  protected readonly result = signal<GenerationResult | null>(null);
  protected readonly selected = signal(0);
  protected readonly examples = signal<string[]>([]);
  protected readonly notice = signal<string | null>(null);
  // Describe with AI: the AI proposes settings; the person approves, corrects or discards them.
  protected readonly areaLabels = AREA_LABELS;
  protected readonly aiStatus = signal<AiStatus | null>(null);
  protected readonly describing = signal(false);
  protected readonly proposing = signal(false);
  protected readonly proposalError = signal<string | null>(null);
  protected readonly proposal = signal<PipelineProposal | null>(null);
  protected readonly fromFile = signal(false);
  protected readonly proposalPreview = signal<GenerationResult | null>(null);
  protected readonly description = signal('');
  protected readonly fresh = signal(false);
  protected readonly correction = signal('');
  protected readonly proposalErrors = computed(() => this.proposal()?.messages.filter(message => message.severity === 'error').length ?? 0);
  protected customCommands = '';
  protected pathFilters = '';
  protected alsoTestWith = '';
  protected readonly services = SERVICES;

  protected readonly errors = computed(() => this.result()?.messages.filter(message => message.severity === 'error') ?? []);
  protected readonly warnings = computed(() => this.result()?.messages.filter(message => message.severity === 'warning') ?? []);
  // While a proposal is under review, the code shown is the proposal's.
  protected readonly files = computed(() => (this.proposal() ? this.proposalPreview() : this.result())?.files ?? []);
  protected readonly file = computed<GeneratedFile | null>(() => {
    const files = this.files();
    return files[Math.min(this.selected(), files.length - 1)] ?? null;
  });

  constructor() {
    void this.start();
  }

  private async start(): Promise<void> {
    this.examples.set(await firstValueFrom(this.http.get<string[]>('/api/examples')));
    this.aiStatus.set(await firstValueFrom(this.http.get<AiStatus>('/api/ai/status')));
    let saved: PipelineSpec | null = null;
    try {
      // Kept for this tab only: a reload keeps the work, a new tab or window starts empty.
      localStorage.removeItem(SAVED);
      const text = sessionStorage.getItem(SAVED);
      saved = text ? (JSON.parse(text) as PipelineSpec) : null;
    } catch {
      saved = null;
    }

    this.load(saved ?? (await firstValueFrom(this.http.get<PipelineSpec>('/api/example', { params: { name: 'new' } }))));
  }

  protected get isContainer(): boolean {
    return ['containerApp', 'aks', 'awsEcs', 'gcpCloudRun', 'gcpGke', 'awsEks', 'kubernetes', 'containerRegistry', 'awsAppRunner', 'gitOps'].includes(this.spec.deploy.target);
  }

  // The targets that can split traffic between the old version and the new one.
  protected get supportsStrategy(): boolean {
    return ['appService', 'functionApp', 'containerApp', 'gcpCloudRun', 'awsLambda', 'awsElasticBeanstalk'].includes(this.spec.deploy.target) || this.isKubernetes || this.isWindows;
  }

  protected get isKubernetes(): boolean {
    return ['aks', 'gcpGke', 'awsEks', 'kubernetes'].includes(this.spec.deploy.target);
  }

  // Docker Hub, ghcr.io or another registry, instead of a cloud's own.
  protected get isGenericRegistry(): boolean {
    return ['kubernetes', 'containerRegistry', 'gitOps'].includes(this.spec.deploy.target);
  }

  protected get usesHelm(): boolean {
    return this.isKubernetes && !!this.spec.deploy.helmChart?.trim();
  }

  protected get isCloud(): boolean {
    return this.isAws || this.spec.deploy.target.startsWith('gcp') || this.spec.deploy.target === 'firebaseHosting' || (this.usesInfraCloud && this.spec.deploy.infraCloud === 'gcp');
  }

  protected get isAws(): boolean {
    const target = this.spec.deploy.target;
    return target.startsWith('aws') || target === 'cloudFormation' || (this.usesInfraCloud && this.spec.deploy.infraCloud === 'aws');
  }

  // Terraform and Pulumi manage the cloud the person chooses.
  protected get usesInfraCloud(): boolean {
    return this.spec.deploy.target === 'pulumi' || this.spec.deploy.target === 'terraform';
  }

  // Elastic Beanstalk and ECS name two things per environment.
  protected get groupLabel(): string {
    switch (this.spec.deploy.target) {
      case 'awsElasticBeanstalk': return 'Elastic Beanstalk application';
      case 'awsEcs': return 'ECS cluster';
      default: return 'Resource group';
    }
  }

  protected get isInfrastructure(): boolean {
    return ['terraform', 'bicep', 'armTemplate', 'cloudFormation', 'awsCdk', 'pulumi'].includes(this.spec.deploy.target);
  }

  protected get usesSlot(): boolean {
    return this.spec.deploy.target === 'appService' || this.spec.deploy.target === 'functionApp' || (this.spec.deploy.target === 'awsElasticBeanstalk' && this.spec.deploy.strategy === 'blueGreen');
  }

  protected get needsGroup(): boolean {
    return !['terraform', 'staticWebApp', 'windowsService', 'iis', 'scheduledTask', 'linuxService', 'script', 'package', 'awsS3', 'awsLambda', 'gcpCloudRun', 'gcpCloudFunctions', 'gcpAppEngine', 'gcpGke', 'awsEks', 'kubernetes', 'containerRegistry', 'cloudFormation', 'awsCdk', 'pulumi', 'googlePlay', 'appStore', 'microsoftStore', 'netlify', 'vercel', 'cloudflarePages', 'firebaseHosting', 'awsSam', 'awsAppRunner', 'gitOps', 'digitalOcean', 'database', 'heroku', 'serviceFabric'].includes(this.spec.deploy.target);
  }

  protected get isStore(): boolean {
    return ['googlePlay', 'appStore', 'microsoftStore'].includes(this.spec.deploy.target);
  }

  // Windows service and IIS run on the environment's own servers.
  protected get isWindows(): boolean {
    return ['windowsService', 'iis', 'scheduledTask', 'linuxService'].includes(this.spec.deploy.target);
  }

  protected get isServer(): boolean {
    return (this.isWindows && !this.spec.deploy.remote) || (this.spec.deploy.target === 'script' && this.spec.deploy.scriptOnServer);
  }

  protected get needsAzure(): boolean {
    const target = this.spec.deploy.target;
    if (target === 'script') {
      return this.spec.deploy.scriptSignsInToAzure && !this.spec.deploy.scriptOnServer;
    }

    return !this.isCloud && !['staticWebApp', 'windowsService', 'iis', 'scheduledTask', 'linuxService', 'package', 'kubernetes', 'containerRegistry', 'googlePlay', 'appStore', 'microsoftStore', 'netlify', 'vercel', 'cloudflarePages', 'gitOps', 'digitalOcean', 'database', 'heroku', 'serviceFabric'].includes(target);
  }

  protected get selfHostedBuild(): boolean {
    return this.spec.build.agent === 'selfHostedLinux' || this.spec.build.agent === 'selfHostedWindows' || this.spec.build.agent === 'selfHostedMac';
  }

  protected get usesSettings(): boolean {
    return ['appService', 'functionApp', 'containerApp', 'windowsService', 'iis', 'scheduledTask', 'linuxService', 'script'].includes(this.spec.deploy.target);
  }

  protected addSetting(environment: EnvironmentSpec): void {
    environment.settings = [...environment.settings, { name: '', value: '', secret: null }];
    this.changed();
  }

  protected removeSetting(environment: EnvironmentSpec, setting: EnvironmentSetting): void {
    environment.settings = environment.settings.filter(item => item !== setting);
    this.changed();
  }

  // A setting is either a value or the name of a secret, never both.
  protected toggleSecret(setting: EnvironmentSetting, secret: boolean): void {
    setting.secret = secret ? '' : null;
    setting.value = secret ? null : '';
    this.changed();
  }

  protected get commandShell(): string {
    return this.spec.deploy.target !== 'linuxService' && (this.isWindows || this.isServer) ? 'PowerShell' : 'bash';
  }

  protected get targetHint(): string {
    return this.targets.find(target => target.value === this.spec.deploy.target)?.hint ?? '';
  }

  protected get flowHint(): string {
    return this.flows.find(flow => flow.value === this.spec.flow)?.hint ?? '';
  }

  protected uses(platform: Platform): boolean {
    return this.spec.platforms.includes(platform);
  }

  protected togglePlatform(platform: Platform, on: boolean): void {
    this.spec.platforms = on ? [...this.spec.platforms, platform] : this.spec.platforms.filter(value => value !== platform);
    this.changed();
  }

  protected hasService(kind: ServiceKind): boolean {
    return (this.spec.build.services ?? []).includes(kind);
  }

  protected toggleService(kind: ServiceKind, on: boolean): void {
    const current = this.spec.build.services ?? [];
    this.spec.build.services = on ? [...current.filter(item => item !== kind), kind] : current.filter(item => item !== kind);
    this.changed();
  }

  protected linesChanged(): void {
    this.spec.build.customCommands = lines(this.customCommands);
    this.spec.pathFilters = lines(this.pathFilters);
    this.spec.build.alsoTestWith = lines(this.alsoTestWith);
    this.changed();
  }

  protected addEnvironment(): void {
    const taken = new Set(this.spec.environments.map(environment => environment.name));
    const name = ['dev', 'test', 'staging', 'prod', 'qa', 'uat'].find(candidate => !taken.has(candidate)) ?? `env${this.spec.environments.length + 1}`;
    const last = this.spec.environments.at(-1);
    this.spec.environments = [...this.spec.environments, {
      name,
      branch: this.spec.mainBranch,
      requireApproval: false,
      resourceGroup: last?.resourceGroup?.replace(last.name, name) ?? null,
      resourceName: last?.resourceName?.replace(last.name, name) ?? null,
      slot: null,
      serviceConnection: last?.serviceConnection?.replace(last.name, name) ?? null,
      parametersFile: last?.parametersFile?.replace(last.name, name) ?? null,
      serverLabel: last?.serverLabel?.replace(last.name, name) ?? null,
      targetPath: last?.targetPath ?? null,
      appPool: last?.appPool?.replace(last.name, name) ?? null,
      healthCheckUrl: last?.healthCheckUrl?.replace(last.name, name) ?? null,
      deployWindow: last?.deployWindow ?? null,
      endToEndFolder: null,
      loadTestScript: null,
      beforeDeploy: [...(last?.beforeDeploy ?? [])],
      afterDeploy: [...(last?.afterDeploy ?? [])],
      servers: [],
      hosts: [],
      settings: (last?.settings ?? []).map(setting => ({ ...setting })),
    }];
    this.changed();
  }

  protected addFeed(): void {
    const taken = new Set(this.spec.build.packageFeeds.map(feed => feed.name));
    const name = ['company', 'internal', 'private'].find(candidate => !taken.has(candidate)) ?? `feed${this.spec.build.packageFeeds.length + 1}`;
    this.spec.build.packageFeeds = [...this.spec.build.packageFeeds, {
      name,
      type: this.feedType,
      kind: 'azureArtifacts',
      organization: null,
      project: null,
      feed: null,
      url: null,
      scope: null,
      inRepositoryConfig: false,
      jFrogOidcProvider: null,
    }];
    this.changed();
  }

  // The kind of packages the build's language uses.
  protected get feedType(): FeedType {
    switch (this.spec.build.stack) {
      case 'node': return 'npm';
      case 'java': return 'maven';
      case 'python': return 'pip';
      case 'gradle': case 'android': return 'maven';
      case 'go': return 'go';
      case 'ruby': return 'rubyGems';
      case 'php': return 'composer';
      case 'rust': return 'cargo';
      case 'elixir': return 'hex';
      case 'flutter': return 'pub';
      case 'swift': return 'swiftRegistry';
      case 'cpp': return 'conan';
      case 'scala': return 'maven';
      default: return 'nuGet';
    }
  }

  protected removeFeed(feed: PackageFeed): void {
    this.spec.build.packageFeeds = this.spec.build.packageFeeds.filter(item => item !== feed);
    this.changed();
  }

  protected feedKindHint(kind: FeedKind): string {
    return this.feedKinds.find(option => option.value === kind)?.hint ?? '';
  }

  protected removeEnvironment(environment: EnvironmentSpec): void {
    this.spec.environments = this.spec.environments.filter(item => item !== environment);
    this.changed();
  }

  protected moveEnvironment(index: number, step: -1 | 1): void {
    const environments = [...this.spec.environments];
    const [item] = environments.splice(index, 1);
    environments.splice(index + step, 0, item);
    this.spec.environments = environments;
    this.changed();
  }

  private feedsFor = '';

  protected changed(): void {
    // The feeds belong to the language: when it changes, the ones kept become feeds of the new language's packages.
    if (this.feedsFor && this.feedsFor !== this.spec.build.stack) {
      for (const feed of this.spec.build.packageFeeds) {
        feed.type = this.feedType;
      }
    }
    this.feedsFor = this.spec.build.stack;

    // A Service Fabric application is packaged by MSBuild, which needs Windows.
    if (this.spec.deploy.target === 'serviceFabric' && this.spec.build.customCommands.length === 0 && !this.spec.build.agent.toLowerCase().includes('windows')) {
      this.spec.build.agent = 'hostedWindows';
    }

    // Publishing a package needs the feed it goes to.
    if (this.spec.deploy.target === 'package' && !this.spec.deploy.publishFeed) {
      this.spec.deploy.publishFeed = {
        name: 'publish', type: this.spec.build.stack === 'node' ? 'npm' : 'nuGet', kind: 'azureArtifacts',
        organization: null, project: null, feed: null, url: null, scope: null, inRepositoryConfig: false, jFrogOidcProvider: null,
      };
    }

    this.revision.update(value => value + 1);
    try {
      sessionStorage.setItem(SAVED, JSON.stringify(this.spec));
    } catch {
      // The browser may refuse storage; the screen works without it.
    }

    clearTimeout(this.timer);
    this.timer = setTimeout(() => void this.generate(), 250);
  }

  private async generate(): Promise<void> {
    const revision = this.revision();
    const result = await firstValueFrom(this.http.post<GenerationResult>('/api/generate', this.spec));
    if (revision === this.revision()) {
      this.result.set(result);
    }
  }

  private load(spec: PipelineSpec): void {
    // Settings saved before package feeds existed have no list.
    spec.build.packageFeeds ??= [];
    spec.deploy.keepOnServer ??= [];
    spec.deploy.deployCommands ??= [];
    spec.deploy.stopTimeoutSeconds ??= 120;
    spec.deploy.settingsFile ??= 'appsettings.json';
    spec.deploy.migrations ??= 'none';
    spec.deploy.migrationsFolder ??= 'db/migrations';
    spec.build.agent ??= 'hostedLinux';
    spec.tagPattern ??= 'v*';
    spec.deploy.appYaml ??= 'app.yaml';
    spec.deploy.registryKind ??= 'dockerHub';
    spec.build.exportOptions ??= 'ExportOptions.plist';
    spec.build.mobileFramework ??= 'native';
    spec.deploy.infraCloud ??= 'azure';
    spec.deploy.strategy ??= 'standard';
    spec.deploy.canaryPercent ??= 10;
    spec.deploy.canaryMinutes ??= 5;
    for (const environment of spec.environments) {
      environment.beforeDeploy ??= [];
      environment.afterDeploy ??= [];
      environment.servers ??= [];
      environment.hosts ??= [];
      environment.settings ??= [];
    }
    this.spec = spec;
    this.feedsFor = spec.build.stack;
    this.customCommands = spec.build.customCommands.join('\n');
    this.pathFilters = spec.pathFilters.join('\n');
    spec.build.services ??= [];
    spec.build.alsoTestWith ??= [];
    this.alsoTestWith = spec.build.alsoTestWith.join('\n');
    this.selected.set(0);
    this.changed();
  }

  /** Starts over: forgets the settings kept in the browser and opens an empty pipeline. */
  protected async newPipeline(): Promise<void> {
    if (!confirm('Start a new pipeline? The current settings are cleared.')) {
      return;
    }

    try {
      sessionStorage.removeItem(SAVED);
    } catch {
      // Nothing was kept.
    }

    this.load(await firstValueFrom(this.http.get<PipelineSpec>('/api/example', { params: { name: 'new' } })));
  }

  protected async example(event: Event): Promise<void> {
    const select = event.target as HTMLSelectElement;
    const name = select.value;
    select.value = '';
    if (name && confirm('Open the example? The current settings are replaced.')) {
      this.load(await firstValueFrom(this.http.get<PipelineSpec>('/api/example', { params: { name } })));
    }
  }

  protected save(): void {
    download(new Blob([JSON.stringify(this.spec, null, 2)], { type: 'application/json' }), `${this.spec.name}.cicd-pipeline-generator.json`);
  }

  protected async open(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) {
      return;
    }

    try {
      this.load(JSON.parse(await file.text()) as PipelineSpec);
    } catch {
      this.flash('That file is not a CI/CD Pipeline Generator settings file.');
    }
  }

  protected async downloadAll(): Promise<void> {
    const blob = await firstValueFrom(this.http.post('/api/export', this.spec, { responseType: 'blob' }));
    download(blob, `${this.spec.name}-pipelines.zip`);
  }

  protected downloadFile(): void {
    const file = this.file();
    if (file) {
      download(new Blob([file.content], { type: 'text/plain' }), file.path.split('/').at(-1)!);
    }
  }

  protected async copy(): Promise<void> {
    const file = this.file();
    if (file) {
      await navigator.clipboard.writeText(file.content);
      this.flash(`Copied ${file.path}`);
    }
  }

  protected openDescribe(): void {
    this.proposalError.set(null);
    this.describing.set(true);
  }

  // An existing pipeline file is read into proposed settings, reviewed like an AI proposal.
  protected async importYaml(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    // The pipeline file, and with it the templates or included files it uses.
    const files = await Promise.all(Array.from(input.files ?? []).map(file => file.text()));
    input.value = '';
    if (files.length === 0) {
      return;
    }

    try {
      const proposal = await firstValueFrom(this.http.post<PipelineProposal>('/api/import', { content: files[0], related: files.slice(1), baseline: this.spec }));
      this.fromFile.set(true);
      this.proposal.set(proposal);
      this.proposalPreview.set(await firstValueFrom(this.http.post<GenerationResult>('/api/generate', proposal.spec)));
      this.selected.set(0);
    } catch (error) {
      const problem = error instanceof HttpErrorResponse ? (error.error as { problem?: string } | null)?.problem : null;
      this.flash(problem ?? 'That file could not be read.');
    }
  }

  protected async propose(): Promise<void> {
    if (await this.ask(this.description().trim(), this.fresh() ? null : this.spec, this.fresh())) {
      this.describing.set(false);
      this.description.set('');
    }
  }

  // A correction starts from the proposal under review; the changes still compare to the settings before it.
  protected async correct(): Promise<void> {
    if (await this.ask(this.correction().trim(), this.proposal()!.spec, false)) {
      this.correction.set('');
    }
  }

  private async ask(description: string, current: PipelineSpec | null, startFresh: boolean): Promise<boolean> {
    this.proposing.set(true);
    this.proposalError.set(null);
    try {
      const proposal = await firstValueFrom(this.http.post<PipelineProposal>('/api/ai/propose', {
        description, current, startFresh, baseline: startFresh ? null : this.spec,
      }));
      this.fromFile.set(false);
      this.proposal.set(proposal);
      this.proposalPreview.set(await firstValueFrom(this.http.post<GenerationResult>('/api/generate', proposal.spec)));
      this.selected.set(0);
      return true;
    } catch (error) {
      const problem = error instanceof HttpErrorResponse ? (error.error as { problem?: string } | null)?.problem : null;
      this.proposalError.set(problem ?? 'The AI could not be reached. Is CI/CD Pipeline Generator still running?');
      return false;
    } finally {
      this.proposing.set(false);
    }
  }

  protected approve(): void {
    const proposal = this.proposal();
    if (proposal) {
      this.load(proposal.spec);
      this.discard();
      this.flash('Proposal approved');
    }
  }

  protected discard(): void {
    this.proposal.set(null);
    this.proposalPreview.set(null);
    this.proposalError.set(null);
    this.correction.set('');
  }

  protected linesOf(event: Event): string[] {
    return lines(this.textOf(event));
  }

  protected textOf(event: Event): string {
    return (event.target as HTMLTextAreaElement).value;
  }

  private flash(text: string): void {
    this.notice.set(text);
    setTimeout(() => this.notice.set(null), 2500);
  }
}

function lines(text: string): string[] {
  return text.split('\n').map(line => line.trim()).filter(line => line.length > 0);
}

function download(blob: Blob, name: string): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  link.click();
  URL.revokeObjectURL(url);
}
