import { Routes } from '@angular/router';
import { FleetOverviewComponent } from './fleet-overview/fleet-overview.component';
import { MaintenanceComponent } from './maintenance/maintenance.component';
import { VehicleDetailComponent } from './vehicle-detail/vehicle-detail.component';

export const routes: Routes = [
  { path: '', component: FleetOverviewComponent, title: 'FleetPulse · Fleet' },
  { path: 'vehicles/:id', component: VehicleDetailComponent, title: 'FleetPulse · Truck' },
  { path: 'maintenance', component: MaintenanceComponent, title: 'FleetPulse · Maintenance' },
  { path: '**', redirectTo: '' },
];
