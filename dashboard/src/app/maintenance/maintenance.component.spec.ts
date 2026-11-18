import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { API_BASE } from '../fleet-api.service';
import { MaintenanceComponent } from './maintenance.component';

describe('MaintenanceComponent', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [MaintenanceComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([]), { provide: API_BASE, useValue: '/api' }],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  // REQ: FR-15
  it('lists the queue in the order the API ranks it, with actions and faults', () => {
    const fixture = TestBed.createComponent(MaintenanceComponent);
    fixture.detectChanges();
    http.expectOne('/api/maintenance').flush([
      { vehicle: 'trk22', band: 'down', risk: null, system: 'Cooling', action: 'Inspect fan clutch', activeFaults: ['SPN 110 FMI 0: Engine coolant temperature'] },
      { vehicle: 'trk16', band: 'high', risk: 0.93, system: 'Fuel', action: 'Replace fuel filters', activeFaults: [] },
    ]);
    fixture.detectChanges();
    const cards: HTMLElement[] = Array.from(fixture.nativeElement.querySelectorAll('li.card'));
    expect(cards.length).toBe(2);
    expect(cards[0].classList).toContain('down');
    expect(cards[0].textContent).toContain('SPN 110 FMI 0');
    expect(cards[1].textContent).toContain('risk 93%');
    expect(cards[1].querySelector('.action')!.textContent).toBe('Replace fuel filters');
  });

  // REQ: FR-15
  it('says when no truck needs the workshop', () => {
    const fixture = TestBed.createComponent(MaintenanceComponent);
    fixture.detectChanges();
    http.expectOne('/api/maintenance').flush([]);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('No truck needs workshop time');
  });
});
