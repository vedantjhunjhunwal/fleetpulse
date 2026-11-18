import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <header>
      <span class="brand">FleetPulse</span>
      <nav>
        <a routerLink="/" routerLinkActive="on" [routerLinkActiveOptions]="{ exact: true }">Fleet</a>
        <a routerLink="/maintenance" routerLinkActive="on">Maintenance</a>
      </nav>
    </header>
    <main><router-outlet /></main>
  `,
})
export class AppComponent {}
