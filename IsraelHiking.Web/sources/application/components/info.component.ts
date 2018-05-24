﻿import { Component } from "@angular/core";
import { Router } from "@angular/router";
import { MatDialog } from "@angular/material";
import "rxjs/add/operator/take";

import { BaseMapComponent } from "./base-map.component";
import { ResourcesService } from "../services/resources.service";
import { SidebarService } from "../services/sidebar.service";
import { HashService, RouteStrings } from "../services/hash.service";
import { DownloadDialogComponent } from "./dialogs/download-dialog.component";

@Component({
    selector: "info",
    templateUrl: "./info.component.html"
})
export class InfoComponent extends BaseMapComponent {

    constructor(resources: ResourcesService,
        private readonly router: Router,
        private readonly hashService: HashService,
        private readonly sidebarService: SidebarService,
        private readonly dialog: MatDialog) {
        super(resources);

        this.hashService.applicationStateChanged.filter(f => f.type === "download")
            .subscribe(() => {
                if (!this.isActive()) {
                    this.sidebarService.toggle("info");
                }
                this.openDownloadDialog();
            });
    }

    public toggleInfo = (e: Event) => {
        this.sidebarService.toggle("info");
        this.suppressEvents(e);
    };

    public isActive = (): boolean => {
        return this.sidebarService.viewName === "info";
    }

    private openDownloadDialog = () => {
        let dialog = this.dialog.open(DownloadDialogComponent, { width: "600px" });
        dialog.afterClosed().take(1).subscribe(() => {
            this.router.navigate([RouteStrings.ROUTE_ROOT]);
        });
    }
}

