import { Injectable } from '@angular/core';
import { Observable } from 'rxjs/Observable';
import { Subject } from 'rxjs/Subject';

@Injectable()
export class LoadingService {
    loading: boolean;
    private subject = new Subject<boolean>();
    constructor() { }
    public setLoading(loading: boolean) {
        this.loading = loading;
        this.subject.next(this.loading);
    }
    public getLoading(): Observable<boolean> {
        return this.subject.asObservable();
    }
}
